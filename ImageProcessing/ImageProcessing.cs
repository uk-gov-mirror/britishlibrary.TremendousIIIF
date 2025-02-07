﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SkiaSharp;
using System.IO;
using Image.Common;
using TremendousIIIF.Common;
using System.Net.Http;
using Serilog;
using Conf = TremendousIIIF.Common.Configuration;
using RotationCoords = System.ValueTuple<float, float, float, int, int>;
using System.Runtime.InteropServices;

namespace ImageProcessing
{
    public class ImageProcessing
    {
        public HttpClient HttpClient { get; set; }
        public ILogger Log { get; set; }

        private static Dictionary<ImageFormat, SKEncodedImageFormat> FormatLookup = new Dictionary<ImageFormat, SKEncodedImageFormat> {
            { ImageFormat.jpg, SKEncodedImageFormat.Jpeg },
            { ImageFormat.png, SKEncodedImageFormat.Png },
            { ImageFormat.webp, SKEncodedImageFormat.Webp },
            // GIF appears to be unsupported on Windows in Skia currently
            // { ImageFormat.gif, SKEncodedImageFormat.Gif }
        };

        /// <summary>
        /// Process image pipeline
        /// <para>Region THEN Size THEN Rotation THEN Quality THEN Format</para>
        /// </summary>
        /// <param name="imageUri">The <see cref="Uri"/> of the source image</param>
        /// <param name="request">The parsed and validated IIIF Image API request</param>
        /// <param name="quality">Image output encoding quality settings</param>
        /// <param name="allowSizeAboveFull">Allow output image dimensions to exceed that of the source image</param>
        /// <param name="pdfMetadata">Optional PDF metadata fields</param>
        /// <returns></returns>
        public async Task<Stream> ProcessImage(Uri imageUri, ImageRequest request, Conf.ImageQuality quality, bool allowSizeAboveFull, Conf.PdfMetadata pdfMetadata)
        {
            var encodingStrategy = GetEncodingStrategy(request.Format);
            if (encodingStrategy == EncodingStrategy.Unknown)
            {
                throw new ArgumentException("Unsupported format", "format");
            }

            var loader = new ImageLoader { HttpClient = HttpClient, Log = Log };
            (var state, var imageRegion) = await loader.ExtractRegion(imageUri, request, allowSizeAboveFull, quality);

            using (imageRegion)
            {
                var expectedWidth = state.OutputWidth;
                var expectedHeight = state.OutputHeight;
                var alphaType = request.Quality == ImageQuality.bitonal ? SKAlphaType.Opaque : SKAlphaType.Premul;

                (var angle, var originX, var originY, var newImgWidth, var newImgHeight) = Rotate(expectedWidth, expectedHeight, request.Rotation.Degrees);

                using (var surface = SKSurface.Create(width: newImgWidth, height: newImgHeight, colorType: SKImageInfo.PlatformColorType, alphaType: alphaType))
                using (var canvas = surface.Canvas)
                using (var region = new SKRegion())
                {
                    // If the rotation parameter includes mirroring ("!"), the mirroring is applied before the rotation.
                    if (request.Rotation.Mirror)
                    {
                        canvas.Translate(newImgWidth, 0);
                        canvas.Scale(-1, 1);
                    }

                    canvas.Translate(originX, originY);
                    canvas.RotateDegrees(angle, 0, 0);

                    // reset clip rects to rotated boundaries
                    region.SetRect(new SKRectI(0 - (int)originX, 0 - (int)originY, newImgWidth, newImgHeight));
                    canvas.ClipRegion(region);

                    // quality
                    if (request.Quality == ImageQuality.gray || request.Quality == ImageQuality.bitonal)
                    {
                        var contrast = request.Quality == ImageQuality.gray ? 0.1f : 1f;
                        using (var cf = SKColorFilter.CreateHighContrast(true, SKHighContrastConfigInvertStyle.NoInvert, contrast))
                        using (var paint = new SKPaint())
                        {
                            paint.FilterQuality = SKFilterQuality.High;
                            paint.ColorFilter = cf;

                            canvas.DrawImage(imageRegion, new SKRect(0, 0, expectedWidth, expectedHeight), paint);
                        }
                    }
                    else
                    {
                        using (var paint = new SKPaint())
                        {
                            paint.FilterQuality = SKFilterQuality.High;
                            canvas.DrawImage(imageRegion, new SKRect(0, 0, expectedWidth, expectedHeight), paint);
                        }
                    }

                    return Encode(surface,
                        expectedWidth,
                        expectedHeight,
                        encodingStrategy,
                        request.Format,
                        quality.GetOutputFormatQuality(request.Format),
                        pdfMetadata,
                        state.HorizontalResolution,
                        state.VerticalResolution);
                }
            }
        }

        private static Stream Encode(SKSurface surface, int width, int height, EncodingStrategy encodingStrategy, ImageFormat format, int q, Conf.PdfMetadata pdfMetadata, ushort horizontalResolution, ushort verticalResolution)
        {
            switch (encodingStrategy)
            {
                case EncodingStrategy.Skia:
                    FormatLookup.TryGetValue(format, out SKEncodedImageFormat formatType);
                    return EncodeSkiaImage(surface, formatType, q, horizontalResolution, verticalResolution);
                case EncodingStrategy.PDF:
                    return EncodePdf(surface, width, height, q, pdfMetadata);
                case EncodingStrategy.JPEG2000:
                    return Jpeg2000.Compressor.Compress(surface.Snapshot());
                case EncodingStrategy.Tifflib:
                    return Image.Tiff.TiffEncoder.Encode(surface.Snapshot());
                default:
                    throw new ArgumentException("Unsupported format", "format");
            }
        }
        /// <summary>
        /// Rotate an image by arbitary degrees, to fit within supplied bounding box.
        /// </summary>
        /// <param name="width">Target width (pixels) of output image</param>
        /// <param name="height">Target height (pixels) of output image</param>
        /// <param name="degrees">arbitary precision degrees of rotation</param>
        /// <returns></returns>
        private static RotationCoords Rotate(int width, int height, float degrees)
        {
            // make it a NOOP for most common requests
            if (degrees == 0 || degrees == 360)
            {
                return (0, 0, 0, width, height);
            }

            var angle = degrees % 360;
            if (angle > 180)
                angle -= 360;
            float sin = (float)Math.Abs(Math.Sin(angle * Math.PI / 180.0)); // this function takes radians
            float cos = (float)Math.Abs(Math.Cos(angle * Math.PI / 180.0)); // this one too
            float newImgWidth = sin * height + cos * width;
            float newImgHeight = sin * width + cos * height;
            float originX = 0f;
            float originY = 0f;

            if (angle > 0)
            {
                if (angle <= 90)
                    originX = sin * height;
                else
                {
                    originX = newImgWidth;
                    originY = newImgHeight - sin * width;
                }
            }
            else
            {
                if (angle >= -90)
                    originY = sin * width;
                else
                {
                    originX = newImgWidth - sin * height;
                    originY = newImgHeight;
                }
            }

            return (angle, originX, originY, (int)newImgWidth, (int)newImgHeight);
        }

        public static Stream EncodeSkiaImage(SKSurface surface, SKEncodedImageFormat format, int q, ushort horizontalResolution, ushort verticalResolution)
        {
            var output = new MemoryStream();
            using (var image = surface.Snapshot())
            {
                var data = image.Encode(format, q);
                if (format == SKEncodedImageFormat.Jpeg)
                {
                    SetJpgDpi(data.Data, horizontalResolution, verticalResolution);
                }
                
                var ms = data.AsStream(false);
                ms.Seek(0, SeekOrigin.Begin);
                return ms;
            }
        }

        private static void SetJpgDpi(IntPtr jpgData, ushort horizontalResolution, ushort verticalResolution)
        {
            // The Skia JPEG encoder sets a default 96x96 DPI, so reset to original DPI

            // JPEG HEADER
            // SOI byte[2]
            // APP0 marker byte[2]
            // Length byte[2]
            // Identifier byte[5] = JIFF in ASCII followed by null byte
            // JIFF Version byte[2]
            // Density units byte[1]
            // XDensity byte[2]
            // YDensity byte[2]
            // 00 01 02 03 04 05 06 07 08 09 10 11 12 13 14 15 16 17
            // FF D8 FF EO 00 10 4A 46 49 46 00 01 02 DD XX XX YY YY
            //...
            // TODO: clean this up using Span<T>
            var header = new byte[18];

            Marshal.Copy(jpgData, header, 0, 18);

            var type = new byte[] { 0x01 };
            byte[] h_dpi = BitConverter.GetBytes(horizontalResolution);
            byte[] v_dpi = BitConverter.GetBytes(verticalResolution);

            // JPEG is big endian
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(h_dpi);
                Array.Reverse(v_dpi);
            }

            Buffer.BlockCopy(type, 0, header, 13, 1);
            Buffer.BlockCopy(h_dpi, 0, header, 14, 2);
            Buffer.BlockCopy(v_dpi, 0, header, 16, 2);

            Marshal.Copy(header, 0, jpgData, 18);
        }

        private static void SetPngDpi(IntPtr pngData, long dataLength, ushort horizontalResolution, ushort verticalResolution)
        {
            // PNG
            // The pHYs chunk specifies the intended pixel size or aspect ratio for display of the image.It contains:

            // Pixels per unit, X axis: 4 bytes(unsigned integer)
            // Pixels per unit, Y axis: 4 bytes(unsigned integer)
            // Unit specifier:          1 byte
            // PNG uses pixels per m

            // need to read each chunk until we find pHYs, then overwrite it
            // EXCEPT that the Skia PNG encoder doesn't set it at all. will have to add it in, much more complicated :(
            var offset = 8; // skip header

            var chunkHeader = new byte[8];

            for (var i = 0L; i < dataLength; i++)
            {
                Marshal.Copy(IntPtr.Add(pngData, offset), chunkHeader, 0, 8);
                //if (BitConverter.IsLittleEndian)
                //{
                //    Array.Reverse(chunkHeader);
                //}
                offset += 8;
                var numBytes = SwapEndianness(BitConverter.ToInt32(chunkHeader, 0));
                var ctype = System.Text.Encoding.ASCII.GetString(chunkHeader, 4, 4);

                if ("IDAT" == ctype)
                {
                    // pHYs must appear before first IDAT
                    break;
                }

                if ("pHYs" != ctype)
                {
                    // data[?] plus crc[4]
                    offset += numBytes + 4;
                    continue;
                }
                var chunkData = new byte[numBytes];
                Marshal.Copy(IntPtr.Add(pngData, offset), chunkData, offset, numBytes);

                // change the data
                var type = new byte[] { 0x01 }; // pixels per metre!
                var ppmh = horizontalResolution / 0.0254;
                var ppmv = verticalResolution / 0.0254;
                byte[] h_dpi = BitConverter.GetBytes(ppmh);
                byte[] v_dpi = BitConverter.GetBytes(ppmv);
                Buffer.BlockCopy(h_dpi, 0, chunkData, 0, 4);
                Buffer.BlockCopy(v_dpi, 0, chunkData, 4, 4);
                Buffer.BlockCopy(type, 0, chunkData, 8, 1);

                var newcrc = Crc32(chunkData, 0, numBytes, idatCrc);

                Marshal.Copy(chunkData, 0, pngData + offset, numBytes);
                Marshal.Copy(BitConverter.GetBytes(newcrc), 0, pngData + offset + numBytes, 4);
                break;
            }

        }

        public static int SwapEndianness(int value)
        {
            var b1 = (value >> 0) & 0xff;
            var b2 = (value >> 8) & 0xff;
            var b3 = (value >> 16) & 0xff;
            var b4 = (value >> 24) & 0xff;

            return b1 << 24 | b2 << 16 | b3 << 8 | b4 << 0;
        }

        static uint[] crcTable;

        // Stores a running CRC (initialized with the CRC of "IDAT" string). When
        // you write this to the PNG, write as a big-endian value
        static uint idatCrc = Crc32(new byte[] { (byte)'I', (byte)'D', (byte)'A', (byte)'T' }, 0, 4, 0);

        // Call this function with the compressed image bytes, 
        // passing in idatCrc as the last parameter
        private static uint Crc32(byte[] stream, int offset, int length, uint crc)
        {
            uint c;
            if (crcTable == null)
            {
                crcTable = new uint[256];
                for (uint n = 0; n <= 255; n++)
                {
                    c = n;
                    for (var k = 0; k <= 7; k++)
                    {
                        if ((c & 1) == 1)
                            c = 0xEDB88320 ^ ((c >> 1) & 0x7FFFFFFF);
                        else
                            c = ((c >> 1) & 0x7FFFFFFF);
                    }
                    crcTable[n] = c;
                }
            }
            c = crc ^ 0xffffffff;
            var endOffset = offset + length;
            for (var i = offset; i < endOffset; i++)
            {
                c = crcTable[(c ^ stream[i]) & 255] ^ ((c >> 8) & 0xFFFFFF);
            }
            return c ^ 0xffffffff;
        }

        /// <summary>
        /// Encode output image as PDF
        /// </summary>
        /// <param name="surface"></param>
        /// <param name="width">Requested output width (pixels)</param>
        /// <param name="height">Requested output height (pixels)</param>
        /// <param name="q">Image quality (percentage)</param>
        /// <param name="pdfMetadata">Optional metadata to include in the PDF</param>
        /// <returns></returns>
        public static Stream EncodePdf(SKSurface surface, int width, int height, int q, Conf.PdfMetadata pdfMetadata)
        {
            // have to encode to JPEG then paint the encoded bytes, otherwise you get full JP2 quality
            var output = new MemoryStream();

            var metadata = new SKDocumentPdfMetadata()
            {
                Creation = DateTime.Now,

            };

            if (null != pdfMetadata)
            {
                metadata.Author = pdfMetadata.Author;
            }

            using (var skstream = new SKManagedWStream(output))
            using (var writer = SKDocument.CreatePdf(skstream, metadata))
            using (var snapshot = surface.Snapshot())
            using (var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, q))
            using (var image = SKImage.FromEncodedData(data))
            using (var paint = new SKPaint())
            {
                using (var canvas = writer.BeginPage(width, height))
                {
                    paint.FilterQuality = SKFilterQuality.High;
                    canvas.DrawImage(image, new SKRect(0, 0, width, height), paint);
                    writer.EndPage();
                }
            }
            output.Seek(0, SeekOrigin.Begin);
            return output;
        }

        /// <summary>
        /// Load source image and extract enough Metadata to create an info.json
        /// </summary>
        /// <param name="imageUri">The <see cref="Uri"/> of the source image</param>
        /// <param name="defaultTileWidth">The default tile width (pixels) to use for the source image, if source is not natively tiled</param>
        /// <param name="requestId">The <code>X-RequestId</code> value to include on any subsequent HTTP calls</param>
        /// <returns></returns>
        public async Task<Metadata> GetImageInfo(Uri imageUri, int defaultTileWidth, string requestId)
        {
            var loader = new ImageLoader { HttpClient = HttpClient, Log = Log };
            return await loader.GetMetadata(imageUri, defaultTileWidth, requestId);
        }

        /// <summary>
        /// Determines output encoding strategy based on supplied <paramref name="format"/>
        /// </summary>
        /// <param name="format">Requested output format type</param>
        /// <returns><see cref="EncodingStrategy"/></returns>
        private static EncodingStrategy GetEncodingStrategy(ImageFormat format)
        {
            if (FormatLookup.ContainsKey(format))
            {
                return EncodingStrategy.Skia;
            }
            if (ImageFormat.pdf == format)
            {
                return EncodingStrategy.PDF;
            }
            if (ImageFormat.jp2 == format)
            {
                return EncodingStrategy.JPEG2000;
            }
            if (ImageFormat.tif == format)
            {
                return EncodingStrategy.Tifflib;
            }

            return EncodingStrategy.Unknown;
        }

        enum EncodingStrategy
        {
            Unknown,
            Skia,
            PDF,
            JPEG2000,
            Tifflib
        }
    }
}
