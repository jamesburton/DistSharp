using DistSharp.Providers.Onnx;
using FluentAssertions;
using Xunit;

namespace DistSharp.Providers.Tests.Onnx;

public sealed class OnnxProviderOptionsTests
{
    [Theory]
    [InlineData("cuda")]
    [InlineData("directml")]
    [InlineData("vulkan")]
    [InlineData("gpu")]
    public void Validate_ThrowsNotSupported_ForNonCpuAccelerators(string accelerator)
    {
        var options = new OnnxProviderOptions { Accelerator = accelerator };

        var act = () => options.Validate();

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*Phase 1 supports CPU only*");
    }

    [Theory]
    [InlineData("cpu")]
    [InlineData("CPU")]
    [InlineData("Cpu")]
    public void Validate_DoesNotThrow_ForCpuAccelerator(string accelerator)
    {
        var options = new OnnxProviderOptions { Accelerator = accelerator };

        var act = () => options.Validate();

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_DoesNotThrow_WhenAcceleratorIsNull()
    {
        var options = new OnnxProviderOptions { Accelerator = null };

        var act = () => options.Validate();

        act.Should().NotThrow();
    }
}
