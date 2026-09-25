#include "pch.h"
#include "PixelConvert.h"

namespace
{
    inline uint8_t Clamp8(int v) { return static_cast<uint8_t>(v < 0 ? 0 : (v > 255 ? 255 : v)); }

    // BT.601 "studio swing" integer coefficients (the usual camera/NV12 convention).
    inline uint8_t LumaOf(int r, int g, int b) { return Clamp8(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16); }
    inline int CbOf(int r, int g, int b) { return ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128; }
    inline int CrOf(int r, int g, int b) { return ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128; }

    inline void Split(uint32_t p, int& r, int& g, int& b)
    {
        r = static_cast<int>((p >> 16) & 0xFF);
        g = static_cast<int>((p >> 8) & 0xFF);
        b = static_cast<int>(p & 0xFF);
    }
}

namespace msv
{
    void ConvertBgraToNv12(const uint32_t* src, uint32_t width, uint32_t height, uint8_t* dstY, LONG pitch)
    {
        uint8_t* dstUv = dstY + static_cast<ptrdiff_t>(pitch) * height;
        for (uint32_t y = 0; y < height; y += 2)
        {
            const uint32_t* row0 = src + static_cast<size_t>(y) * width;
            const uint32_t* row1 = row0 + width;
            uint8_t* y0 = dstY + static_cast<ptrdiff_t>(pitch) * y;
            uint8_t* y1 = y0 + pitch;
            uint8_t* uv = dstUv + static_cast<ptrdiff_t>(pitch) * (y / 2);
            // Fast path: the frame is mostly one background colour, so uniform 2x2 blocks reuse the
            // values computed for the previous uniform block instead of redoing the matrix math.
            uint32_t uniformColor = 0;
            uint8_t uniformY = 0, uniformU = 0, uniformV = 0;
            bool haveUniform = false;
            for (uint32_t x = 0; x < width; x += 2)
            {
                const uint32_t p = row0[x];
                if (p == row0[x + 1] && p == row1[x] && p == row1[x + 1])
                {
                    if (!haveUniform || p != uniformColor)
                    {
                        int r, g, b;
                        Split(p, r, g, b);
                        uniformColor = p;
                        uniformY = LumaOf(r, g, b);
                        uniformU = Clamp8(CbOf(r, g, b));
                        uniformV = Clamp8(CrOf(r, g, b));
                        haveUniform = true;
                    }

                    y0[x] = y0[x + 1] = y1[x] = y1[x + 1] = uniformY;
                    uv[x] = uniformU;
                    uv[x + 1] = uniformV;
                    continue;
                }

                int r00, g00, b00, r01, g01, b01, r10, g10, b10, r11, g11, b11;
                Split(row0[x], r00, g00, b00);
                Split(row0[x + 1], r01, g01, b01);
                Split(row1[x], r10, g10, b10);
                Split(row1[x + 1], r11, g11, b11);
                y0[x] = LumaOf(r00, g00, b00);
                y0[x + 1] = LumaOf(r01, g01, b01);
                y1[x] = LumaOf(r10, g10, b10);
                y1[x + 1] = LumaOf(r11, g11, b11);
                int cb = CbOf(r00, g00, b00) + CbOf(r01, g01, b01) + CbOf(r10, g10, b10) + CbOf(r11, g11, b11);
                int cr = CrOf(r00, g00, b00) + CrOf(r01, g01, b01) + CrOf(r10, g10, b10) + CrOf(r11, g11, b11);
                uv[x] = Clamp8((cb + 2) / 4);
                uv[x + 1] = Clamp8((cr + 2) / 4);
            }
        }
    }

    void CopyBgraToRgb32(const uint32_t* src, uint32_t width, uint32_t height, uint8_t* dstRow0, LONG pitch)
    {
        const size_t rowBytes = static_cast<size_t>(width) * 4;
        for (uint32_t y = 0; y < height; y++)
        {
            memcpy(dstRow0 + static_cast<ptrdiff_t>(pitch) * y, src + static_cast<size_t>(y) * width, rowBytes);
        }
    }

    void FillNv12(uint32_t argb, uint32_t width, uint32_t height, uint8_t* dstY, LONG pitch)
    {
        int r, g, b;
        Split(argb, r, g, b);
        const uint8_t luma = LumaOf(r, g, b);
        const uint8_t cb = Clamp8(CbOf(r, g, b));
        const uint8_t cr = Clamp8(CrOf(r, g, b));
        for (uint32_t y = 0; y < height; y++)
        {
            memset(dstY + static_cast<ptrdiff_t>(pitch) * y, luma, width);
        }

        uint8_t* dstUv = dstY + static_cast<ptrdiff_t>(pitch) * height;
        for (uint32_t y = 0; y < height / 2; y++)
        {
            uint8_t* uv = dstUv + static_cast<ptrdiff_t>(pitch) * y;
            for (uint32_t x = 0; x < width; x += 2)
            {
                uv[x] = cb;
                uv[x + 1] = cr;
            }
        }
    }

    void FillRgb32(uint32_t argb, uint32_t width, uint32_t height, uint8_t* dstRow0, LONG pitch)
    {
        const uint32_t pixel = argb | 0xFF000000u;
        for (uint32_t y = 0; y < height; y++)
        {
            uint32_t* row = reinterpret_cast<uint32_t*>(dstRow0 + static_cast<ptrdiff_t>(pitch) * y);
            for (uint32_t x = 0; x < width; x++)
            {
                row[x] = pixel;
            }
        }
    }

    void ConvertNv12ToBgra(const uint8_t* srcY, LONG pitch, uint32_t width, uint32_t height, uint32_t* dst)
    {
        const uint8_t* srcUv = srcY + static_cast<ptrdiff_t>(pitch) * height;
        for (uint32_t y = 0; y < height; y++)
        {
            const uint8_t* yRow = srcY + static_cast<ptrdiff_t>(pitch) * y;
            const uint8_t* uvRow = srcUv + static_cast<ptrdiff_t>(pitch) * (y / 2);
            uint32_t* out = dst + static_cast<size_t>(y) * width;
            for (uint32_t x = 0; x < width; x++)
            {
                const int c = yRow[x] - 16;
                const int d = uvRow[x & ~1u] - 128;
                const int e = uvRow[(x & ~1u) + 1] - 128;
                const uint32_t r = Clamp8((298 * c + 409 * e + 128) >> 8);
                const uint32_t g = Clamp8((298 * c - 100 * d - 208 * e + 128) >> 8);
                const uint32_t b = Clamp8((298 * c + 516 * d + 128) >> 8);
                out[x] = 0xFF000000u | (r << 16) | (g << 8) | b;
            }
        }
    }

    void CopyRgb32ToBgra(const uint8_t* srcRow0, LONG pitch, uint32_t width, uint32_t height, uint32_t* dst)
    {
        for (uint32_t y = 0; y < height; y++)
        {
            const uint32_t* row = reinterpret_cast<const uint32_t*>(srcRow0 + static_cast<ptrdiff_t>(pitch) * y);
            uint32_t* out = dst + static_cast<size_t>(y) * width;
            for (uint32_t x = 0; x < width; x++)
            {
                out[x] = row[x] | 0xFF000000u;
            }
        }
    }
}
