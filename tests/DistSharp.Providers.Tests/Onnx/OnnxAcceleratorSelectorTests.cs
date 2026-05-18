using DistSharp.Providers.Onnx;
using FluentAssertions;
using Xunit;

namespace DistSharp.Providers.Tests.Onnx;

public sealed class OnnxAcceleratorSelectorTests
{
    [Fact]
    public void SelectAccelerator_AlwaysReturnsCpu()
    {
        var result = OnnxAcceleratorSelector.SelectAccelerator();

        result.Should().Be("cpu");
    }
}
