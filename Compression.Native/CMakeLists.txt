cmake_minimum_required(VERSION 3.25)

project(ZiperNativeGpu LANGUAGES CXX)

option(ZIPER_NATIVEGPU_WITH_NVCOMP "実験的なCUDA/nvCOMPバックエンドをビルドする。" ON)
option(ZIPER_NATIVEGPU_EXPERIMENTAL_SINGLE_CHUNK_DEFLATE "小さなZIPエントリ向けの実験的raw DEFLATE exportを有効にする。" OFF)

if (MSVC)
    # ポータブル版がVC++再頒布可能パッケージを要求しないようにする。
    set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()

add_library(EZConverter.NativeGpu SHARED)
target_compile_features(EZConverter.NativeGpu PRIVATE cxx_std_17)
target_include_directories(EZConverter.NativeGpu PRIVATE include)
if (MSVC)
    target_compile_options(EZConverter.NativeGpu PRIVATE /utf-8)
endif()
set_target_properties(EZConverter.NativeGpu PROPERTIES
    OUTPUT_NAME "EZConverter.NativeGpu"
    WINDOWS_EXPORT_ALL_SYMBOLS OFF)

if (WIN32)
    target_compile_definitions(EZConverter.NativeGpu PRIVATE ZIPER_NATIVEGPU_WINDOWS=1)
endif()

if (ZIPER_NATIVEGPU_WITH_NVCOMP)
    target_sources(EZConverter.NativeGpu PRIVATE src/ziper_native_gpu_dynamic.cpp)
    target_compile_definitions(EZConverter.NativeGpu PRIVATE ZIPER_NATIVEGPU_WITH_NVCOMP=1)
    if (ZIPER_NATIVEGPU_EXPERIMENTAL_SINGLE_CHUNK_DEFLATE)
        target_compile_definitions(EZConverter.NativeGpu PRIVATE ZIPER_NATIVEGPU_EXPERIMENTAL_SINGLE_CHUNK_DEFLATE=1)
    endif()
else()
    target_sources(EZConverter.NativeGpu PRIVATE src/ziper_native_gpu_stub.cpp)
endif()
