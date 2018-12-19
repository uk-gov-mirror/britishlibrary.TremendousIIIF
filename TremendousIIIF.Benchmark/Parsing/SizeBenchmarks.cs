﻿using BenchmarkDotNet.Attributes;
using Image.Common;
using System;
using TremendousIIIF.Validation;

namespace TremendousIIIF.Benchmark.Parsing
{
    [HtmlExporter, CsvExporter, RPlotExporter]
    [Config(typeof(MultipleRuntimes))]
    [MemoryDiagnoser]
    public class SizeBenchmarks
    {
        [Params("full", "max", "pct:10", "pct:25.5444712736684", "256,256", "!256,256", "256,", ",256", "^max", "^pct:110", "^!1024,1024")]
        public string RegionString;

        [Benchmark]
        public ImageSize Default()
        {
            try
            {
                return ImageRequestValidator.CalculateSize(RegionString);
            }
            catch (Exception)

            {
                return new ImageSize();
            }
        }
    }
}
