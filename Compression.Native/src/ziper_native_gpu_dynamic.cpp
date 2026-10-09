#include "ziper_native_gpu.h"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>

#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#endif

namespace
{
constexpr size_t kSingleChunkLimit = 64 * 1024;
constexpr size_t kBatchTotalLimit = 128 * 1024 * 1024;
constexpr int kCudaSuccess = 0;
constexpr int kNvcompSuccess = 0;
constexpr unsigned int kCudaStreamNonBlocking = 1;
constexpr int kCudaMemcpyHostToDevice = 1;
constexpr int kCudaMemcpyDeviceToHost = 2;

using cudaError_t = int;
using cudaStream_t = void*;
using nvcompStatus_t = int;

struct nvcompBatchedDeflateCompressOpts_t
{
    int algorithm;
    char reserved[60];
};

thread_local std::string g_last_error;

void set_last_error(const char* message)
{
    g_last_error = message == nullptr ? "" : message;
}

void set_last_error(const std::string& message)
{
    g_last_error = message;
}

int map_algorithm(int compression_level)
{
    switch (compression_level)
    {
    case 1:
        return 1;
    case 2:
        return 2;
    case 3:
        return 4;
    default:
        return 1;
    }
}

int fail(int status, const char* message)
{
    set_last_error(message);
    return status;
}

#if defined(_WIN32)
using LibraryHandle = HMODULE;

std::string directory_name(const std::string& path)
{
    const auto slash = path.find_last_of("\\/");
    return slash == std::string::npos ? std::string() : path.substr(0, slash);
}

std::string combine_path(const std::string& directory, const std::string& file_name)
{
    if (directory.empty())
    {
        return {};
    }

    const auto last = directory.back();
    return (last == '\\' || last == '/')
        ? directory + file_name
        : directory + "\\" + file_name;
}

bool file_exists(const std::string& path)
{
    const auto attributes = GetFileAttributesA(path.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0;
}

std::string get_environment_variable(const char* name)
{
    const auto required = GetEnvironmentVariableA(name, nullptr, 0);
    if (required == 0)
    {
        return {};
    }

    std::string value(required, '\0');
    const auto written = GetEnvironmentVariableA(name, value.data(), required);
    if (written == 0)
    {
        return {};
    }

    value.resize(written);
    return value;
}

void append_unique_directory(std::vector<std::string>& directories, const std::string& directory)
{
    if (directory.empty())
    {
        return;
    }

    if (std::find(directories.begin(), directories.end(), directory) == directories.end())
    {
        directories.push_back(directory);
    }
}

std::string current_module_directory()
{
    HMODULE module = nullptr;
    if (!GetModuleHandleExA(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCSTR>(&current_module_directory),
            &module))
    {
        return {};
    }

    char buffer[MAX_PATH] = {};
    const auto length = GetModuleFileNameA(module, buffer, static_cast<DWORD>(sizeof(buffer)));
    return length == 0 ? std::string() : directory_name(std::string(buffer, length));
}

std::string executable_directory()
{
    char buffer[MAX_PATH] = {};
    const auto length = GetModuleFileNameA(nullptr, buffer, static_cast<DWORD>(sizeof(buffer)));
    return length == 0 ? std::string() : directory_name(std::string(buffer, length));
}

void append_application_search_directories(std::vector<std::string>& directories)
{
    constexpr const char* runtime = "win-x64";

    const auto exe_dir = executable_directory();
    const auto module_dir = current_module_directory();
    append_unique_directory(directories, exe_dir);
    append_unique_directory(directories, combine_path(combine_path(combine_path(exe_dir, "runtimes"), runtime), "native"));
    append_unique_directory(directories, module_dir);
    append_unique_directory(directories, combine_path(module_dir, "bin"));
    append_unique_directory(directories, combine_path(combine_path(module_dir, "nvcomp"), "bin"));
    append_unique_directory(directories, combine_path(combine_path(exe_dir, "nvcomp"), "bin"));
}

void append_cuda_search_directories(std::vector<std::string>& directories)
{
    append_application_search_directories(directories);
    const auto cuda_path = get_environment_variable("CUDA_PATH");
    append_unique_directory(directories, combine_path(cuda_path, "bin"));
}

void append_nvcomp_search_directories(std::vector<std::string>& directories)
{
    append_application_search_directories(directories);

    const auto nvcomp_root = get_environment_variable("NVCOMP_ROOT");
    append_unique_directory(directories, nvcomp_root);
    append_unique_directory(directories, combine_path(nvcomp_root, "bin"));
    append_unique_directory(directories, combine_path(nvcomp_root, "lib"));
    append_unique_directory(directories, combine_path(combine_path(nvcomp_root, "lib"), "x64"));

    const auto program_files = get_environment_variable("ProgramFiles");
    append_unique_directory(directories, combine_path(combine_path(combine_path(program_files, "NVIDIA"), "nvCOMP"), "bin"));
    append_unique_directory(directories, combine_path(combine_path(combine_path(program_files, "NVIDIA Corporation"), "nvCOMP"), "bin"));
    append_unique_directory(directories, combine_path(combine_path(program_files, "nvCOMP"), "bin"));
}

std::string describe_directories(const std::vector<std::string>& directories)
{
    if (directories.empty())
    {
        return "none";
    }

    std::string result;
    for (const auto& directory : directories)
    {
        if (!result.empty())
        {
            result += "; ";
        }

        result += directory;
    }

    return result;
}

LibraryHandle load_library_from_path(const std::string& path)
{
    return LoadLibraryExA(path.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
}

LibraryHandle load_library_by_name(const char* name)
{
    return LoadLibraryA(name);
}

template <size_t Count>
LibraryHandle load_library_from_candidates(
    const std::array<const char*, Count>& names,
    const std::vector<std::string>& directories,
    std::string& loaded_path)
{
    for (const auto& directory : directories)
    {
        for (const auto* name : names)
        {
            const auto path = combine_path(directory, name);
            if (!file_exists(path))
            {
                continue;
            }

            auto library = load_library_from_path(path);
            if (library != nullptr)
            {
                loaded_path = path;
                return library;
            }
        }
    }

    loaded_path.clear();
    return nullptr;
}

void* load_symbol(LibraryHandle library, const char* name)
{
    return library == nullptr ? nullptr : reinterpret_cast<void*>(GetProcAddress(library, name));
}
#else
using LibraryHandle = void*;

std::string describe_directories(const std::vector<std::string>&)
{
    return "未対応のプラットフォーム";
}

void append_cuda_search_directories(std::vector<std::string>&)
{
}

void append_nvcomp_search_directories(std::vector<std::string>&)
{
}

template <size_t Count>
LibraryHandle load_library_from_candidates(
    const std::array<const char*, Count>&,
    const std::vector<std::string>&,
    std::string&)
{
    return nullptr;
}

void* load_symbol(LibraryHandle, const char*)
{
    return nullptr;
}
#endif

template <typename T>
bool bind_symbol(LibraryHandle library, const char* name, T& target)
{
    target = reinterpret_cast<T>(load_symbol(library, name));
    return target != nullptr;
}

struct DynamicGpuApi
{
    using CudaStreamCreateWithFlags = cudaError_t(__cdecl*)(cudaStream_t*, unsigned int);
    using CudaStreamDestroy = cudaError_t(__cdecl*)(cudaStream_t);
    using CudaStreamSynchronize = cudaError_t(__cdecl*)(cudaStream_t);
    using CudaMalloc = cudaError_t(__cdecl*)(void**, size_t);
    using CudaFree = cudaError_t(__cdecl*)(void*);
    using CudaMemcpy = cudaError_t(__cdecl*)(void*, const void*, size_t, int);
    using CudaMemcpyAsync = cudaError_t(__cdecl*)(void*, const void*, size_t, int, cudaStream_t);
    using CudaGetErrorString = const char*(__cdecl*)(cudaError_t);

    using NvcompGetMaxOutputChunkSize = nvcompStatus_t(__cdecl*)(
        size_t,
        nvcompBatchedDeflateCompressOpts_t,
        size_t*);
    using NvcompGetTempSizeAsync = nvcompStatus_t(__cdecl*)(
        size_t,
        size_t,
        nvcompBatchedDeflateCompressOpts_t,
        size_t*,
        size_t);
    using NvcompCompressAsync = nvcompStatus_t(__cdecl*)(
        const void* const*,
        const size_t*,
        size_t,
        size_t,
        void*,
        size_t,
        void* const*,
        size_t*,
        nvcompBatchedDeflateCompressOpts_t,
        nvcompStatus_t*,
        cudaStream_t);

    LibraryHandle cuda_library = nullptr;
    LibraryHandle nvcomp_library = nullptr;
    bool attempted = false;
    bool available = false;
    std::string error;

    CudaStreamCreateWithFlags cudaStreamCreateWithFlags = nullptr;
    CudaStreamDestroy cudaStreamDestroy = nullptr;
    CudaStreamSynchronize cudaStreamSynchronize = nullptr;
    CudaMalloc cudaMalloc = nullptr;
    CudaFree cudaFree = nullptr;
    CudaMemcpy cudaMemcpy = nullptr;
    CudaMemcpyAsync cudaMemcpyAsync = nullptr;
    CudaGetErrorString cudaGetErrorString = nullptr;

    NvcompGetMaxOutputChunkSize nvcompBatchedDeflateCompressGetMaxOutputChunkSize = nullptr;
    NvcompGetTempSizeAsync nvcompBatchedDeflateCompressGetTempSizeAsync = nullptr;
    NvcompCompressAsync nvcompBatchedDeflateCompressAsync = nullptr;

    bool load()
    {
        static std::mutex load_mutex;
        std::lock_guard<std::mutex> lock(load_mutex);
        if (attempted)
        {
            if (!available && !error.empty())
            {
                set_last_error(error);
            }
            return available;
        }

        attempted = true;
        const std::array<const char*, 5> cuda_names = {
            "cudart64_13.dll",
            "cudart64_12.dll",
            "cudart64_11.dll",
            "cudart64_10.dll",
            "cudart64_65.dll"};
        std::vector<std::string> cuda_directories;
        append_cuda_search_directories(cuda_directories);
        std::string cuda_loaded_path;
        cuda_library = load_library_from_candidates(cuda_names, cuda_directories, cuda_loaded_path);

        if (cuda_library == nullptr)
        {
            error = "CUDA Runtime DLL が見つかりません。検索先: " + describe_directories(cuda_directories);
            set_last_error(error);
            return false;
        }

        const std::array<const char*, 6> nvcomp_names = {
            "nvcomp64_5.dll",
            "nvcomp64_4.dll",
            "nvcomp64_3.dll",
            "nvcomp64_2.dll",
            "nvcomp64.dll",
            "nvcomp.dll"};
        std::vector<std::string> nvcomp_directories;
        append_nvcomp_search_directories(nvcomp_directories);
        std::string nvcomp_loaded_path;
        nvcomp_library = load_library_from_candidates(nvcomp_names, nvcomp_directories, nvcomp_loaded_path);

        if (nvcomp_library == nullptr)
        {
            error = "nvCOMP DLL が見つかりません。検索先: " + describe_directories(nvcomp_directories);
            set_last_error(error);
            return false;
        }

        if (!bind_symbol(cuda_library, "cudaStreamCreateWithFlags", cudaStreamCreateWithFlags) ||
            !bind_symbol(cuda_library, "cudaStreamDestroy", cudaStreamDestroy) ||
            !bind_symbol(cuda_library, "cudaStreamSynchronize", cudaStreamSynchronize) ||
            !bind_symbol(cuda_library, "cudaMalloc", cudaMalloc) ||
            !bind_symbol(cuda_library, "cudaFree", cudaFree) ||
            !bind_symbol(cuda_library, "cudaMemcpy", cudaMemcpy) ||
            !bind_symbol(cuda_library, "cudaMemcpyAsync", cudaMemcpyAsync) ||
            !bind_symbol(cuda_library, "cudaGetErrorString", cudaGetErrorString))
        {
            error = "CUDA Runtime の必要なexportが不足しています。";
            set_last_error(error);
            return false;
        }

        if (!bind_symbol(nvcomp_library, "nvcompBatchedDeflateCompressGetMaxOutputChunkSize", nvcompBatchedDeflateCompressGetMaxOutputChunkSize) ||
            !bind_symbol(nvcomp_library, "nvcompBatchedDeflateCompressGetTempSizeAsync", nvcompBatchedDeflateCompressGetTempSizeAsync) ||
            !bind_symbol(nvcomp_library, "nvcompBatchedDeflateCompressAsync", nvcompBatchedDeflateCompressAsync))
        {
            error = "nvCOMP Deflate の必要なexportが不足しています。";
            set_last_error(error);
            return false;
        }

        available = true;
        error.clear();
        set_last_error("");
        return true;
    }

    std::string cuda_error(cudaError_t status, const char* operation) const
    {
        const auto* message = cudaGetErrorString == nullptr ? nullptr : cudaGetErrorString(status);
        return std::string(operation) + ": " + (message == nullptr ? std::to_string(status) : message);
    }
};

DynamicGpuApi& api()
{
    static DynamicGpuApi instance;
    return instance;
}

int fail_cuda(cudaError_t status, const char* operation)
{
    set_last_error(api().cuda_error(status, operation));
    return -2000 - status;
}

int fail_nvcomp(nvcompStatus_t status, const char* operation)
{
    set_last_error(std::string(operation) + " failed with nvCOMP status " + std::to_string(status));
    return -3000 - status;
}

struct DeviceBuffer
{
    void* pointer = nullptr;
    size_t capacity = 0;

    int ensure(size_t required_bytes, const char* operation)
    {
        if (required_bytes == 0 || capacity >= required_bytes)
        {
            return 0;
        }

        release();
        auto cuda_status = api().cudaMalloc(&pointer, required_bytes);
        if (cuda_status != kCudaSuccess)
        {
            pointer = nullptr;
            capacity = 0;
            return fail_cuda(cuda_status, operation);
        }

        capacity = required_bytes;
        return 0;
    }

    void release()
    {
        if (pointer != nullptr && api().cudaFree != nullptr)
        {
            api().cudaFree(pointer);
        }

        pointer = nullptr;
        capacity = 0;
    }

    ~DeviceBuffer()
    {
        // CUDA runtime teardown can happen before C++ static destructors during process exit.
        // Runtime reallocations still call release(); process-exit cleanup is left to the OS/driver.
    }
};

struct BatchWorkspace
{
    std::mutex mutex;
    cudaStream_t stream = nullptr;
    DeviceBuffer input;
    DeviceBuffer output;
    DeviceBuffer temp;
    DeviceBuffer input_ptrs;
    DeviceBuffer output_ptrs;
    DeviceBuffer input_sizes;
    DeviceBuffer output_sizes;
    DeviceBuffer statuses;

    int ensure_stream()
    {
        if (stream != nullptr)
        {
            return 0;
        }

        auto cuda_status = api().cudaStreamCreateWithFlags(&stream, kCudaStreamNonBlocking);
        if (cuda_status != kCudaSuccess)
        {
            stream = nullptr;
            return fail_cuda(cuda_status, "cudaStreamCreateWithFlags batch workspace");
        }

        return 0;
    }

    int ensure_buffers(
        size_t total_input_bytes,
        size_t output_span_bytes,
        size_t item_count,
        size_t temp_bytes)
    {
        auto status = ensure_stream();
        if (status != 0)
        {
            return status;
        }

        status = input.ensure(total_input_bytes, "cudaMalloc batch workspace input");
        if (status != 0)
        {
            return status;
        }

        status = output.ensure(output_span_bytes, "cudaMalloc batch workspace output");
        if (status != 0)
        {
            return status;
        }

        status = input_ptrs.ensure(sizeof(void*) * item_count, "cudaMalloc batch workspace input pointers");
        if (status != 0)
        {
            return status;
        }

        status = output_ptrs.ensure(sizeof(void*) * item_count, "cudaMalloc batch workspace output pointers");
        if (status != 0)
        {
            return status;
        }

        status = input_sizes.ensure(sizeof(size_t) * item_count, "cudaMalloc batch workspace input sizes");
        if (status != 0)
        {
            return status;
        }

        status = output_sizes.ensure(sizeof(size_t) * item_count, "cudaMalloc batch workspace output sizes");
        if (status != 0)
        {
            return status;
        }

        status = statuses.ensure(sizeof(nvcompStatus_t) * item_count, "cudaMalloc batch workspace statuses");
        if (status != 0)
        {
            return status;
        }

        return temp.ensure(temp_bytes, "cudaMalloc batch workspace temp");
    }

    void release()
    {
        statuses.release();
        output_sizes.release();
        input_sizes.release();
        output_ptrs.release();
        input_ptrs.release();
        temp.release();
        output.release();
        input.release();

        if (stream != nullptr && api().cudaStreamDestroy != nullptr)
        {
            api().cudaStreamDestroy(stream);
        }

        stream = nullptr;
    }

    ~BatchWorkspace()
    {
        // See DeviceBuffer::~DeviceBuffer. Avoid CUDA calls from static destruction.
    }
};

BatchWorkspace& batch_workspace()
{
    static BatchWorkspace instance;
    return instance;
}

bool supports_runtime()
{
#if defined(ZIPER_NATIVEGPU_EXPERIMENTAL_SINGLE_CHUNK_DEFLATE)
    return api().load();
#else
    set_last_error("実験的single-chunk Deflateはビルド時に無効化されています。");
    return false;
#endif
}

size_t get_raw_deflate_batch_output_capacity(size_t max_input_length, int compression_level)
{
#if !defined(ZIPER_NATIVEGPU_EXPERIMENTAL_SINGLE_CHUNK_DEFLATE)
    fail(-1002, "このネイティブGPUブリッジでは、batch ZIP互換raw DEFLATEが有効化されていません。");
    return 0;
#else
    if (!api().load())
    {
        fail(-1003, g_last_error.c_str());
        return 0;
    }

    if (max_input_length == 0 || max_input_length > kSingleChunkLimit)
    {
        fail(-11, "nvCOMP Deflate batchの各項目は1から65536バイトである必要があります。");
        return 0;
    }

    nvcompBatchedDeflateCompressOpts_t opts{};
    opts.algorithm = map_algorithm(compression_level);

    size_t max_compressed_chunk_bytes = 0;
    const auto nv_status = api().nvcompBatchedDeflateCompressGetMaxOutputChunkSize(
        max_input_length,
        opts,
        &max_compressed_chunk_bytes);
    if (nv_status != kNvcompSuccess)
    {
        fail_nvcomp(nv_status, "nvcompBatchedDeflateCompressGetMaxOutputChunkSize");
        return 0;
    }

    set_last_error("");
    return (max_compressed_chunk_bytes + 255) & ~size_t(255);
#endif
}

int compress_batch(
    const uint8_t* input_buffer,
    const size_t* input_offsets,
    const size_t* input_lengths,
    size_t item_count,
    int compression_level,
    uint8_t* output_buffer,
    const size_t* output_offsets,
    const size_t* output_capacities,
    size_t* output_lengths)
{
#if !defined(ZIPER_NATIVEGPU_EXPERIMENTAL_SINGLE_CHUNK_DEFLATE)
    return fail(-1002, "このネイティブGPUブリッジでは、batch ZIP互換raw DEFLATEが有効化されていません。");
#else
    if (!api().load())
    {
        return fail(-1003, g_last_error.c_str());
    }

    if (item_count == 0)
    {
        return 0;
    }

    if (input_buffer == nullptr ||
        input_offsets == nullptr ||
        input_lengths == nullptr ||
        output_buffer == nullptr ||
        output_offsets == nullptr ||
        output_capacities == nullptr ||
        output_lengths == nullptr)
    {
        return fail(-10, "batchポインターの一部がnullです。");
    }

    nvcompBatchedDeflateCompressOpts_t opts{};
    opts.algorithm = map_algorithm(compression_level);

    size_t total_input_bytes = 0;
    size_t max_input_bytes = 0;
    size_t output_span_bytes = 0;
    for (size_t i = 0; i < item_count; ++i)
    {
        output_lengths[i] = 0;
        if (input_lengths[i] == 0 || input_lengths[i] > kSingleChunkLimit)
        {
            return fail(-11, "nvCOMP Deflate batchの各項目は1から65536バイトである必要があります。");
        }

        if (input_offsets[i] + input_lengths[i] < input_offsets[i] ||
            output_offsets[i] + output_capacities[i] < output_offsets[i])
        {
            return fail(-12, "batchオフセットがオーバーフローしました。");
        }

        total_input_bytes = std::max(total_input_bytes, input_offsets[i] + input_lengths[i]);
        max_input_bytes = std::max(max_input_bytes, input_lengths[i]);
        output_span_bytes = std::max(output_span_bytes, output_offsets[i] + output_capacities[i]);
    }

    if (total_input_bytes > kBatchTotalLimit)
    {
        return fail(-13, "実験的batchの合計入力上限を超えました。");
    }

    const auto max_compressed_chunk_bytes = get_raw_deflate_batch_output_capacity(max_input_bytes, compression_level);
    if (max_compressed_chunk_bytes == 0)
    {
        return -3001;
    }

    for (size_t i = 0; i < item_count; ++i)
    {
        if (output_capacities[i] < max_compressed_chunk_bytes)
        {
            return fail(-14, "batch出力バッファーの一部が小さすぎます。");
        }
    }

    size_t temp_bytes = 0;
    auto nv_status = api().nvcompBatchedDeflateCompressGetTempSizeAsync(
        item_count,
        max_input_bytes,
        opts,
        &temp_bytes,
        total_input_bytes);
    if (nv_status != kNvcompSuccess)
    {
        return fail_nvcomp(nv_status, "nvcompBatchedDeflateCompressGetTempSizeAsync batch");
    }

    auto& workspace = batch_workspace();
    std::lock_guard<std::mutex> workspace_lock(workspace.mutex);
    auto workspace_status = workspace.ensure_buffers(total_input_bytes, output_span_bytes, item_count, temp_bytes);
    if (workspace_status != 0)
    {
        return workspace_status;
    }

    auto* d_input = static_cast<uint8_t*>(workspace.input.pointer);
    auto* d_output = static_cast<uint8_t*>(workspace.output.pointer);
    auto* d_temp = workspace.temp.pointer;
    auto* d_input_ptrs = static_cast<void**>(workspace.input_ptrs.pointer);
    auto* d_output_ptrs = static_cast<void**>(workspace.output_ptrs.pointer);
    auto* d_input_sizes = static_cast<size_t*>(workspace.input_sizes.pointer);
    auto* d_output_sizes = static_cast<size_t*>(workspace.output_sizes.pointer);
    auto* d_statuses = static_cast<nvcompStatus_t*>(workspace.statuses.pointer);
    auto stream = workspace.stream;

    std::vector<void*> h_input_ptrs(item_count);
    std::vector<void*> h_output_ptrs(item_count);
    for (size_t i = 0; i < item_count; ++i)
    {
        h_input_ptrs[i] = d_input + input_offsets[i];
        h_output_ptrs[i] = d_output + output_offsets[i];
    }

    auto cuda_status = api().cudaMemcpyAsync(d_input, input_buffer, total_input_bytes, kCudaMemcpyHostToDevice, stream);
    if (cuda_status != kCudaSuccess)
    {
        return fail_cuda(cuda_status, "cudaMemcpyAsync batch input");
    }

    cuda_status = api().cudaMemcpyAsync(d_input_ptrs, h_input_ptrs.data(), sizeof(void*) * item_count, kCudaMemcpyHostToDevice, stream);
    if (cuda_status != kCudaSuccess)
    {
        return fail_cuda(cuda_status, "cudaMemcpyAsync batch input pointers");
    }

    cuda_status = api().cudaMemcpyAsync(d_output_ptrs, h_output_ptrs.data(), sizeof(void*) * item_count, kCudaMemcpyHostToDevice, stream);
    if (cuda_status != kCudaSuccess)
    {
        return fail_cuda(cuda_status, "cudaMemcpyAsync batch output pointers");
    }

    cuda_status = api().cudaMemcpyAsync(d_input_sizes, input_lengths, sizeof(size_t) * item_count, kCudaMemcpyHostToDevice, stream);
    if (cuda_status != kCudaSuccess)
    {
        return fail_cuda(cuda_status, "cudaMemcpyAsync batch input sizes");
    }

    nv_status = api().nvcompBatchedDeflateCompressAsync(
        reinterpret_cast<const void* const*>(d_input_ptrs),
        d_input_sizes,
        max_input_bytes,
        item_count,
        d_temp,
        temp_bytes,
        d_output_ptrs,
        d_output_sizes,
        opts,
        d_statuses,
        stream);
    if (nv_status != kNvcompSuccess)
    {
        return fail_nvcomp(nv_status, "nvcompBatchedDeflateCompressAsync batch");
    }

    std::vector<nvcompStatus_t> h_statuses(item_count);
    std::vector<size_t> h_output_sizes(item_count);
    cuda_status = api().cudaMemcpyAsync(h_statuses.data(), d_statuses, sizeof(nvcompStatus_t) * item_count, kCudaMemcpyDeviceToHost, stream);
    if (cuda_status != kCudaSuccess)
    {
        return fail_cuda(cuda_status, "cudaMemcpyAsync batch statuses");
    }

    cuda_status = api().cudaMemcpyAsync(h_output_sizes.data(), d_output_sizes, sizeof(size_t) * item_count, kCudaMemcpyDeviceToHost, stream);
    if (cuda_status != kCudaSuccess)
    {
        return fail_cuda(cuda_status, "cudaMemcpyAsync batch output sizes");
    }

    cuda_status = api().cudaStreamSynchronize(stream);
    if (cuda_status != kCudaSuccess)
    {
        return fail_cuda(cuda_status, "cudaStreamSynchronize batch");
    }

    for (size_t i = 0; i < item_count; ++i)
    {
        if (h_statuses[i] != kNvcompSuccess)
        {
            return fail_nvcomp(h_statuses[i], "nvCOMP Deflate batch chunk");
        }

        if (h_output_sizes[i] > output_capacities[i])
        {
            return fail(-15, "圧縮後のbatch出力が容量を超えました。");
        }

        output_lengths[i] = h_output_sizes[i];
    }

    size_t group_start_offset = 0;
    size_t group_end_offset = 0;
    size_t group_actual_bytes = 0;
    size_t group_items = 0;
    auto flush_output_group = [&]() -> int {
        if (group_items == 0)
        {
            return 0;
        }

        const auto group_span = group_end_offset - group_start_offset;
        cuda_status = api().cudaMemcpyAsync(
            output_buffer + group_start_offset,
            d_output + group_start_offset,
            group_span,
            kCudaMemcpyDeviceToHost,
            stream);
        if (cuda_status != kCudaSuccess)
        {
            return fail_cuda(cuda_status, "cudaMemcpyAsync batch output group");
        }

        group_start_offset = 0;
        group_end_offset = 0;
        group_actual_bytes = 0;
        group_items = 0;
        return 0;
    };

    for (size_t i = 0; i < item_count; ++i)
    {
        if (h_output_sizes[i] == 0)
        {
            continue;
        }

        const auto item_start = output_offsets[i];
        const auto item_end = output_offsets[i] + h_output_sizes[i];
        if (group_items == 0)
        {
            group_start_offset = item_start;
            group_end_offset = item_end;
            group_actual_bytes = h_output_sizes[i];
            group_items = 1;
            continue;
        }

        const auto candidate_end = std::max(group_end_offset, item_end);
        const auto candidate_actual_bytes = group_actual_bytes + h_output_sizes[i];
        const auto candidate_span = candidate_end - group_start_offset;
        if (candidate_actual_bytes <= SIZE_MAX / 2 && candidate_span > candidate_actual_bytes * 2)
        {
            const auto status = flush_output_group();
            if (status != 0)
            {
                return status;
            }

            group_start_offset = item_start;
            group_end_offset = item_end;
            group_actual_bytes = h_output_sizes[i];
            group_items = 1;
            continue;
        }

        group_end_offset = candidate_end;
        group_actual_bytes = candidate_actual_bytes;
        group_items++;
    }

    const auto copy_status = flush_output_group();
    if (copy_status != 0)
    {
        return copy_status;
    }

    cuda_status = api().cudaStreamSynchronize(stream);
    if (cuda_status != kCudaSuccess)
    {
        return fail_cuda(cuda_status, "cudaStreamSynchronize batch output copies");
    }

    set_last_error("");
    return 0;
#endif
}
}

ZIPER_NATIVEGPU_EXPORT int ziper_gpu_supports_zip_deflate(void)
{
    return supports_runtime() ? 1 : 0;
}

ZIPER_NATIVEGPU_EXPORT int ziper_gpu_supports_zip_deflate_batch(void)
{
    return supports_runtime() ? 1 : 0;
}

ZIPER_NATIVEGPU_EXPORT size_t ziper_gpu_get_max_raw_deflate_input_size(void)
{
    return kSingleChunkLimit;
}

ZIPER_NATIVEGPU_EXPORT size_t ziper_gpu_get_max_raw_deflate_batch_total_input_size(void)
{
    return kBatchTotalLimit;
}

ZIPER_NATIVEGPU_EXPORT size_t ziper_gpu_get_raw_deflate_batch_output_capacity(
    size_t max_input_length,
    int compression_level)
{
    return get_raw_deflate_batch_output_capacity(max_input_length, compression_level);
}

ZIPER_NATIVEGPU_EXPORT int ziper_gpu_compress_raw_deflate(
    const uint8_t* input,
    size_t input_length,
    int compression_level,
    uint8_t* output,
    size_t output_capacity,
    size_t* output_length)
{
    size_t input_offset = 0;
    size_t output_offset = 0;
    return compress_batch(
        input,
        &input_offset,
        &input_length,
        1,
        compression_level,
        output,
        &output_offset,
        &output_capacity,
        output_length);
}

ZIPER_NATIVEGPU_EXPORT int ziper_gpu_compress_raw_deflate_batch(
    const uint8_t* input_buffer,
    const size_t* input_offsets,
    const size_t* input_lengths,
    size_t item_count,
    int compression_level,
    uint8_t* output_buffer,
    const size_t* output_offsets,
    const size_t* output_capacities,
    size_t* output_lengths)
{
    return compress_batch(
        input_buffer,
        input_offsets,
        input_lengths,
        item_count,
        compression_level,
        output_buffer,
        output_offsets,
        output_capacities,
        output_lengths);
}

ZIPER_NATIVEGPU_EXPORT int ziper_gpu_compress_raw_deflate_batch_v2(
    const uint8_t* input_buffer,
    size_t input_buffer_offset,
    const size_t* input_offsets,
    const size_t* input_lengths,
    size_t item_count,
    int compression_level,
    uint8_t* output_buffer,
    const size_t* output_offsets,
    const size_t* output_capacities,
    size_t* output_lengths)
{
    return compress_batch(
        input_buffer == nullptr ? nullptr : input_buffer + input_buffer_offset,
        input_offsets,
        input_lengths,
        item_count,
        compression_level,
        output_buffer,
        output_offsets,
        output_capacities,
        output_lengths);
}

ZIPER_NATIVEGPU_EXPORT const char* ziper_gpu_get_last_error(void)
{
    return g_last_error.c_str();
}

// Inputs must have been bounded and validated with a safe CPU decoder before this call.
// nvCOMP explicitly does not guarantee safe behaviour on malformed bitstreams.
ZIPER_NATIVEGPU_EXPORT int ez_gpu_decompress_deflate_batch(
    const uint8_t* input, size_t input_capacity, const size_t* offsets,
    const size_t* sizes, const size_t* expected_sizes, size_t count,
    uint8_t* output, size_t output_capacity)
{
    struct DecompressOptions { int backend; int sort; char reserved[56]; };
    static_assert(sizeof(DecompressOptions) == 64, "nvCOMP 5.2 option ABI");
    using GetTemp = int(__cdecl*)(size_t, size_t, DecompressOptions, size_t*, size_t);
    using Decompress = int(__cdecl*)(const void* const*, const size_t*, const size_t*, size_t*, size_t,
        void*, size_t, void* const*, DecompressOptions, int*, cudaStream_t);
    if (!api().load()) return fail(-1003, g_last_error.c_str());
    GetTemp get_temp = nullptr;
    Decompress decompress = nullptr;
    if (!bind_symbol(api().nvcomp_library, "nvcompBatchedDeflateDecompressGetTempSizeAsync", get_temp) ||
        !bind_symbol(api().nvcomp_library, "nvcompBatchedDeflateDecompressAsync", decompress))
        return fail(-1004, "nvCOMP GPU decompression exports are missing");
    if (count == 0) return 0;
    if (!input || !output || !offsets || !sizes || !expected_sizes || count > 2048)
        return fail(-10, "Invalid decompression batch");
    size_t total_input = 0, total_output = 0, max_output = 0;
    std::vector<size_t> input_offsets(count), output_offsets(count);
    for (size_t i = 0; i < count; ++i) {
        if (offsets[i] > input_capacity || sizes[i] > input_capacity - offsets[i] || sizes[i] == 0 ||
            sizes[i] > 128 * 1024 || expected_sizes[i] == 0 || expected_sizes[i] > kSingleChunkLimit)
            return fail(-11, "Decompression chunk exceeds safe bounds");
        input_offsets[i] = total_input;
        output_offsets[i] = total_output;
        total_input += (sizes[i] + 255) & ~size_t(255);
        total_output += (expected_sizes[i] + 255) & ~size_t(255);
        max_output = std::max(max_output, expected_sizes[i]);
    }
    if (total_input > kBatchTotalLimit || total_output > kBatchTotalLimit || total_output > output_capacity)
        return fail(-12, "Decompression staging exceeds memory limit");
    std::vector<uint8_t> staging(total_input, 0);
    for (size_t i = 0; i < count; ++i)
        std::memcpy(staging.data() + input_offsets[i], input + offsets[i], sizes[i]);
    DecompressOptions opts{};
    opts.backend = 2; // NVCOMP_DECOMPRESS_BACKEND_CUDA, not the optional hardware engine.
    size_t temp_bytes = 0;
    auto ns = get_temp(count, max_output, opts, &temp_bytes, total_output);
    if (ns) return fail_nvcomp(ns, "DeflateDecompressGetTempSizeAsync");
    if (temp_bytes > 1024ULL * 1024 * 1024) return fail(-13, "Decompression scratch exceeds memory limit");
    auto& w = batch_workspace();
    std::lock_guard<std::mutex> lock(w.mutex);
    auto ws = w.ensure_buffers(total_input, total_output, count, temp_bytes);
    if (ws) return ws;
    DeviceBuffer capacities;
    struct BufferCleanup { DeviceBuffer& b; ~BufferCleanup() { b.release(); } } cleanup{capacities};
    ws = capacities.ensure(count * sizeof(size_t), "cudaMalloc output capacities");
    if (ws) return ws;
    std::vector<void*> inputs(count), outputs(count);
    for (size_t i = 0; i < count; ++i) {
        inputs[i] = static_cast<uint8_t*>(w.input.pointer) + input_offsets[i];
        outputs[i] = static_cast<uint8_t*>(w.output.pointer) + output_offsets[i];
    }
    // Synchronous copies avoid pageable host buffers being freed with work still pending on failures.
    auto copy = [&](void* to, const void* from, size_t bytes, int direction) {
        auto cs = api().cudaMemcpy(to, from, bytes, direction);
        return cs ? fail_cuda(cs, "cudaMemcpy decompression") : 0;
    };
    if ((ws = copy(w.input.pointer, staging.data(), total_input, kCudaMemcpyHostToDevice)) ||
        (ws = copy(w.input_ptrs.pointer, inputs.data(), count * sizeof(void*), kCudaMemcpyHostToDevice)) ||
        (ws = copy(w.output_ptrs.pointer, outputs.data(), count * sizeof(void*), kCudaMemcpyHostToDevice)) ||
        (ws = copy(w.input_sizes.pointer, sizes, count * sizeof(size_t), kCudaMemcpyHostToDevice)) ||
        (ws = copy(capacities.pointer, expected_sizes, count * sizeof(size_t), kCudaMemcpyHostToDevice))) return ws;
    ns = decompress(static_cast<const void* const*>(w.input_ptrs.pointer), static_cast<const size_t*>(w.input_sizes.pointer),
        static_cast<const size_t*>(capacities.pointer), static_cast<size_t*>(w.output_sizes.pointer), count,
        w.temp.pointer, temp_bytes, static_cast<void* const*>(w.output_ptrs.pointer), opts,
        static_cast<int*>(w.statuses.pointer), w.stream);
    auto cs = api().cudaStreamSynchronize(w.stream);
    if (ns) return fail_nvcomp(ns, "DeflateDecompressAsync");
    if (cs) return fail_cuda(cs, "cudaStreamSynchronize decompression");
    std::vector<int> statuses(count);
    std::vector<size_t> actual(count);
    if ((ws = copy(statuses.data(), w.statuses.pointer, count * sizeof(int), kCudaMemcpyDeviceToHost)) ||
        (ws = copy(actual.data(), w.output_sizes.pointer, count * sizeof(size_t), kCudaMemcpyDeviceToHost))) return ws;
    for (size_t i = 0; i < count; ++i) {
        if (statuses[i]) return fail_nvcomp(statuses[i], "GPU decompression chunk");
        if (actual[i] != expected_sizes[i]) return fail(-14, "GPU decompressed length mismatch");
    }
    ws = copy(output, w.output.pointer, total_output, kCudaMemcpyDeviceToHost);
    if (ws) return ws;
    set_last_error("");
    return 0;
}
