using System.Threading;
using System.Threading.Tasks;
using YFex.State.Validation;

namespace YFex.State.Tests.Validation;

public class ValidatorContractTests
{
    [Fact]
    public void IValidator_StaticAbstract_DispatchesWithoutInstance()
    {
        var ok = NotEmptyValidator.Validate("ok");
        var bad = NotEmptyValidator.Validate("");

        ok.IsSuccess.Should().BeTrue();
        bad.IsSuccess.Should().BeFalse();
        bad.Message.Should().Be("required");
        bad.PropertyName.Should().Be("Value");
    }

    [Fact]
    public async Task IAsyncValidator_SyncCompletingPath_ReturnsValueTaskWithoutAllocation()
    {
        var t = SyncAsyncValidator.ValidateAsync(5, default);
        t.IsCompletedSuccessfully.Should().BeTrue();
        var result = await t;
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task IAsyncValidator_AsyncPath_AwaitsAndReturnsResult()
    {
        var t = DelayedAsyncValidator.ValidateAsync(0, default);
        t.IsCompleted.Should().BeFalse();
        var result = await t;
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task IAsyncValidator_RespectsCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(20);

        var t = CancellingAsyncValidator.ValidateAsync(0, cts.Token);

        var act = async () => await t;
        await act.Should().ThrowAsync<TaskCanceledException>();
    }

    public sealed class NotEmptyValidator : IValidator<string>
    {
        public static ValidationResult Validate(string value) =>
            string.IsNullOrEmpty(value)
                ? new ValidationResult("required", "Value")
                : ValidationResult.Success;
    }

    public sealed class SyncAsyncValidator : IAsyncValidator<int>
    {
        public static ValueTask<ValidationResult> ValidateAsync(int value, CancellationToken ct) =>
            new(value > 0 ? ValidationResult.Success : new ValidationResult("must be positive", "Value"));
    }

    public sealed class DelayedAsyncValidator : IAsyncValidator<int>
    {
        public static async ValueTask<ValidationResult> ValidateAsync(int value, CancellationToken ct)
        {
            await Task.Delay(20, ct);
            return value > 0 ? ValidationResult.Success : new ValidationResult("nope", "Value");
        }
    }

    public sealed class CancellingAsyncValidator : IAsyncValidator<int>
    {
        public static async ValueTask<ValidationResult> ValidateAsync(int value, CancellationToken ct)
        {
            await Task.Delay(1000, ct);
            return ValidationResult.Success;
        }
    }
}
