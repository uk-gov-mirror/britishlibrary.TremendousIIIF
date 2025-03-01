﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using SkiaSharp;
using Jpeg2000;
using Image.Common;
using Image.Tiff;
using System.Net.Http.Headers;
using TremendousIIIF.Common;
using Serilog;

namespace ImageProcessing
{
    public class ImageLoader
    {
        public HttpClient HttpClient { get; set; }
        public ILogger Log { get; set; }

        /// <summary>
        /// MagicBytes used to identify JPEG2000 or TIFF files (big or little endian) if unsuitable mimetype supplied
        /// </summary>
        private static readonly Dictionary<byte[], ImageFormat> MagicBytes = new Dictionary<byte[], ImageFormat> {
            { new byte[] { 0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A, 0x00, 0x00, 0x00, 0x14, 0x66, 0x74, 0x79, 0x70, 0x6A, 0x70, 0x32 }, ImageFormat.jp2 },
            // little endian
            { new byte[] { 0x49, 0x49, 0x2A, 0x00 }, ImageFormat.tif },
            // big endian
            { new byte [] { 0x4D, 0x4D, 0x00, 0x2A }, ImageFormat.tif }
        };

        /// <summary>
        /// Extract region from source image
        /// </summary>
        /// <param name="imageUri">The <see cref="Uri"/> of the source image</param>
        /// <param name="request">The <see cref="ImageRequest"/></param>
        /// <param name="allowSizeAboveFull">Allow the output size to exceed the original dimensions of the image</param>
        /// <param name="quality">The <see cref="ImageQuality"/> settings for encoding</param>
        /// <returns></returns>
        public async Task<(ProcessState,SKImage)> ExtractRegion(Uri imageUri, ImageRequest request, bool allowSizeAboveFull, TremendousIIIF.Common.Configuration.ImageQuality quality)
        {
            var sourceFormat = await GetSourceFormat(imageUri, request.RequestId);

            switch (sourceFormat)
            {
                case ImageFormat.jp2:
                    return J2KExpander.ExpandRegion(HttpClient, Log, imageUri, request, allowSizeAboveFull, quality);
                case ImageFormat.tif:
                    return TiffExpander.ExpandRegion(HttpClient, Log, imageUri, request, allowSizeAboveFull);
                default:
                    throw new IOException("Unsupported source format");
            }
        }
        /// <summary>
        /// Get Metadata fromt he source image, for info.json requests
        /// </summary>
        /// <param name="imageUri">The <see cref="Uri"/> of the source image</param>
        /// <param name="defaultTileWidth">The default tile width (pixels) if the source image is not natively tiled</param>
        /// <param name="requestId">The correlation ID to include on any subsequent HTTP requests</param>
        /// <returns></returns>
        public async Task<Metadata> GetMetadata(Uri imageUri, int defaultTileWidth, string requestId)
        {
            var sourceFormat = await Task.Run(() => GetSourceFormat(imageUri, requestId));
            switch (sourceFormat)
            {
                case ImageFormat.jp2:
                    return J2KExpander.GetMetadata(HttpClient, Log, imageUri, defaultTileWidth, requestId);
                case ImageFormat.tif:
                    return TiffExpander.GetMetadata(HttpClient, Log, imageUri, defaultTileWidth, requestId);
                default:
                    throw new IOException("Unsupported source format");
            }
        }

        /// <summary>
        /// Determine the image format of the source image, using mimetpye if available, otherwise using <see cref="MagicBytes"/>
        /// </summary>
        /// <param name="imageUri">The <see cref="Uri"/> of the source image</param>
        /// <param name="requestId">The correlation ID to include on any subsequent HTTP requests</param>
        /// <returns></returns>
        public async Task<ImageFormat> GetSourceFormat(Uri imageUri, string requestId)
        {
            var longest = MagicBytes.Keys.Max(k => k.Length);
            if (imageUri.Scheme == "http" || imageUri.Scheme == "https")
            {
                // issue a HEAD request, use supplied mimetype
                using (var headRequest = new HttpRequestMessage(HttpMethod.Head, imageUri))
                {
                    headRequest.Headers.Add("X-Request-ID", requestId);
                    using (var response = await HttpClient.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead))
                    {
                        // Some failures we want to handle differently
                        switch (response.StatusCode)
                        {
                            case System.Net.HttpStatusCode.NotFound:
                                throw new FileNotFoundException("Unable to load source image", imageUri.ToString());
                        }

                        if(!response.IsSuccessStatusCode)
                        {
                            Log.Error("{@ImageUri} {@StatusCode} {@ReasonPhrase}", imageUri, response.StatusCode, response.ReasonPhrase);
                            throw new IOException("Unable to load source image");
                        }

                        if (!response.Content.Headers.TryGetValues("Content-Type", out IEnumerable<string> values))
                        {
                            throw new FileLoadException("Unable to determine source format");
                        }
                        // remove any charset tags
                        var mime = values.First().Split(';').First();

                        if (mime.StartsWith("text/plain") || mime == "application/octet-stream")
                        {
                            // badly configured source server, issue a range request
                            using (var rangeRequest = new HttpRequestMessage(HttpMethod.Get, imageUri))
                            {
                                rangeRequest.Headers.Add("X-Request-Id", requestId);
                                rangeRequest.Headers.Range = new RangeHeaderValue(0, longest);
                                using (var byteResponse = await HttpClient.SendAsync(rangeRequest, HttpCompletionOption.ResponseHeadersRead))
                                {
                                    byteResponse.EnsureSuccessStatusCode();
                                    var fileBytes = await byteResponse.Content.ReadAsByteArrayAsync();
                                    return CompareMagicBytes(fileBytes);
                                }
                            }
                        }
                        var imageFormat = GetFormatFromMimeType(mime);

                        return imageFormat;
                    }
                }
            }
            else if (imageUri.IsFile)
            {
                using (var fs = new FileStream(imageUri.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, longest, true))
                using (var fr = new BinaryReader(fs))
                {
                    var fileBytes = fr.ReadBytes(longest);
                    return CompareMagicBytes(fileBytes);
                }
            }
            throw new IOException("Unsupported scheme");
        }

        /// <summary>
        /// Binary compare of <see cref="MagicBytes"/>
        /// </summary>
        /// <param name="fileBytes">The binary buffer to compare</param>
        /// <returns></returns>
        private static ImageFormat CompareMagicBytes(byte[] fileBytes)
        {
            foreach (var mb in MagicBytes.OrderByDescending(v => v.Key.Length))
            {
                var subBytes = new byte[mb.Key.Length];
                Buffer.BlockCopy(fileBytes, 0, subBytes, 0, mb.Key.Length);

                if (mb.Key.SequenceEqual(subBytes))
                {
                    return mb.Value;
                }
            }
            throw new IOException("Unable to determine source format");
        }

        /// <summary>
        /// Map mimetypes to <see cref="ImageFormat"/>
        /// </summary>
        /// <param name="mimeType">The mimetype to compare</param>
        /// <returns></returns>
        private static ImageFormat GetFormatFromMimeType(string mimeType)
        {
            // still amazes me mimetype mapping isn't properly solved.
            switch (mimeType)
            {
                case "image/jp2":
                case "image/jpeg2000":
                case "image/jpeg2000-image":
                case "image/x-jpeg2000-image":
                    return ImageFormat.jp2;
                case "image/tif":
                case "image/tiff":
                case "image/x-tif":
                case "image/x-tiff":
                case "application/tif":
                case "application/x-tif":
                case "application/tiff":
                case "application/x-tiff":
                    return ImageFormat.tif;
                default:
                    throw new IOException("Unsupported source image format type");
            }
        }
    }
}
