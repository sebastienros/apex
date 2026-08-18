using System.Collections;
using Apex.PgClient.Internal;
using BenchmarkDotNet.Attributes;

namespace Apex.DriverBenchmarks;

[MemoryDiagnoser]
public class BitArrayDecodeBenchmarks
{
    private byte[] _value = null!;

    [Params(8, 32, 128, 1024, 8192)]
    public int Length { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _value = new byte[Length];
        for (var i = 0; i < _value.Length; i++)
        {
            _value[i] = (byte)((i & 1) == 0 ? '0' : '1');
        }
    }

    [Benchmark(Baseline = true)]
    public BitArray Scalar()
    {
        var result = new BitArray(_value.Length);
        for (var i = 0; i < _value.Length; i++)
        {
            result[i] = _value[i] switch
            {
                (byte)'0' => false,
                (byte)'1' => true,
                _ => throw new FormatException(),
            };
        }

        return result;
    }

    [Benchmark]
    public BitArray Vectorized() => PgTextCodec.DecodeBitArray(_value);
}
