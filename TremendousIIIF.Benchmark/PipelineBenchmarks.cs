﻿using BenchmarkDotNet.Attributes;

using Image.Common;
using Serilog;
using System;
using System.IO;
using System.Threading.Tasks;
using TremendousIIIF.Common;

namespace TremendousIIIF.Benchmark
{
    [SimpleJob(launchCount: 1, warmupCount: 1, targetCount: 3)]
    [MemoryDiagnoser]
    public class PipelineBenchmarks
    {
        public ImageProcessing.ImageProcessing IP { get; set; }

        public Uri ImageUri { get; set; }

        public ImageRequest Request { get; set; }

        public Common.Configuration.ImageQuality Quality { get; set; }

        public bool AllowSizeAboveFull { get; set; }
        [GlobalSetup]
        public void Setup()
        {
            var log = new LoggerConfiguration().CreateLogger();
            IP = new ImageProcessing.ImageProcessing(null,  log, new ImageProcessing.ImageLoader(null,  log));
            ImageUri = new Uri("file:///C:/Source/TremendousIIIF/TremendousIIIF.Benchmark/TestData/RoyalMS.jp2");
            Quality = new Common.Configuration.ImageQuality();
            Request = new ImageRequest("", new ImageRegion(ImageRegionMode.Full), new ImageSize(ImageSizeMode.Max, 1), new ImageRotation(0, false), ImageQuality.gray, ImageFormat.jpg);
        }

        [Benchmark]
        public Task<Stream> ProcessImage()
        {
            return IP.ProcessImage(ImageUri, Request, Quality, AllowSizeAboveFull, null);
        }
    }
}