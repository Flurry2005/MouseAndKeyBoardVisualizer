// Pixel format conversion for the media source (and the test consumer).
#pragma once

namespace msv
{
    // BGRA (0xAARRGGBB little-endian, top-down, tightly packed) -> NV12, BT.601 limited range.
    // dstY points at row 0 of the Y plane; the interleaved UV plane starts at dstY + pitch * height.
    // pitch may exceed width (padding). width and height must be even.
    void ConvertBgraToNv12(const uint32_t* src, uint32_t width, uint32_t height, uint8_t* dstY, LONG pitch);

    // BGRA -> RGB32 (identical byte order) with an arbitrary, possibly negative pitch.
    void CopyBgraToRgb32(const uint32_t* src, uint32_t width, uint32_t height, uint8_t* dstRow0, LONG pitch);

    // Solid colour frames (fallback when the app is not producing).
    void FillNv12(uint32_t argb, uint32_t width, uint32_t height, uint8_t* dstY, LONG pitch);
    void FillRgb32(uint32_t argb, uint32_t width, uint32_t height, uint8_t* dstRow0, LONG pitch);

    // Decoders used by the test consumer.
    void ConvertNv12ToBgra(const uint8_t* srcY, LONG pitch, uint32_t width, uint32_t height, uint32_t* dst);
    void CopyRgb32ToBgra(const uint8_t* srcRow0, LONG pitch, uint32_t width, uint32_t height, uint32_t* dst);
}
