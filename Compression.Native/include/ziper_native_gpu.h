#pragma once

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#define ZIPER_NATIVEGPU_EXPORT extern "C" __declspec(dllexport)
#else
#define ZIPER_NATIVEGPU_EXPORT extern "C" __attribute__((visibility("default")))
#endif

ZIPER_NATIVEGPU_EXPORT int ziper_gpu_supports_zip_deflate(void);

ZIPER_NATIVEGPU_EXPORT int ziper_gpu_supports_zip_deflate_batch(void);

// Returns 0 for unlimited. A positive value means the current native backend
// only supports inputs up to that byte count for one ZIP-compatible raw DEFLATE stream.
ZIPER_NATIVEGPU_EXPORT size_t ziper_gpu_get_max_raw_deflate_input_size(void);

ZIPER_NATIVEGPU_EXPORT size_t ziper_gpu_get_max_raw_deflate_batch_total_input_size(void);

ZIPER_NATIVEGPU_EXPORT size_t ziper_gpu_get_raw_deflate_batch_output_capacity(
    size_t max_input_length,
    int compression_level);

ZIPER_NATIVEGPU_EXPORT int ziper_gpu_compress_raw_deflate(
    const uint8_t* input,
    size_t input_length,
    int compression_level,
    uint8_t* output,
    size_t output_capacity,
    size_t* output_length);

ZIPER_NATIVEGPU_EXPORT int ziper_gpu_compress_raw_deflate_batch(
    const uint8_t* input_buffer,
    const size_t* input_offsets,
    const size_t* input_lengths,
    size_t item_count,
    int compression_level,
    uint8_t* output_buffer,
    const size_t* output_offsets,
    const size_t* output_capacities,
    size_t* output_lengths);

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
    size_t* output_lengths);

ZIPER_NATIVEGPU_EXPORT const char* ziper_gpu_get_last_error(void);
