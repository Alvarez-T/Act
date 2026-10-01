using YFex.Cqrs;
using YFex.Cqrs.Configuration;
using YFex.Messaging.Tests.Fixtures;
using YFex.Validation;

namespace YFex.Messaging.Tests.Cqrs;

/// <summary>Validation Phase 3, step 1: null-safe <see cref="Error"/> carrying structured violations (G4, G5).</summary>
[Collection("DispatcherTests")]
public sealed class StructuredErrorTests
{
    public StructuredErrorTests() => TestDispatcherFixture.ClearStore();

    [Fact]
    public void Default_error_is_safe()
    {
        Error e = default;

        e.Type.Should().Be(ErrorType.None);
        e.Message.Should().BeEmpty();
        e.Violations.IsEmpty.Should().BeTrue();
        e.HasViolations.Should().BeFalse();
    }

    [Fact]
    public void Validation_error_keeps_violations_and_a_compatible_message()
    {
        var result = ValidationResult.Of(ViolationList.From(
        [
            new Violation(FieldPath.Of("Nick"), "len", Severity.Warning),
            new Violation(FieldPath.Of("Name"), "required", detail: "Name is required"),
            new Violation(FieldPath.Of("Email"), "email"),
        ]));

        Error e = Error.Validation(result);

        e.Type.Should().Be(ErrorType.ValidationProblem);
        e.Violations.Count.Should().Be(3);
        e.Message.Should().Be("Name: Name is required", "the first ERROR wins, detail preferred over code");
    }

    [Fact]
    public void Validation_error_message_falls_back_to_code()
    {
        Error e = Error.Validation(ValidationResult.Of(new Violation(FieldPath.Empty, "form.incomplete")));

        e.Message.Should().Be("form.incomplete");
    }

    [Fact]
    public async Task Invalid_command_returns_ValidationProblem_with_violations()
    {
        var fx = new TestDispatcherFixture(configurations: [new RequiredNameConfig()]);

        var r = await fx.Dispatcher.CommandAsync<TestAggregate.Commands.CreateCommand, TestItem>(
            new TestAggregate.Commands.CreateCommand(1, ""));

        r.TryGetError(out var error).Should().BeTrue();
        error.Type.Should().Be(ErrorType.ValidationProblem);
        error.Violations.Count.Should().Be(1);
        error.Violations[0].Path.ToString().Should().Be("Name");
        error.Violations[0].Code.Value.Should().Be("required");
        TestDispatcherFixture.Store.Should().BeEmpty("the handler must not run");
    }

    [Fact]
    public async Task Unauthorized_command_returns_ErrorType_Unauthorized()
    {
        var fx = new TestDispatcherFixture(configurations: [new AuthenticatedDeleteConfig()]);

        var r = await fx.Dispatcher.CommandAsync(new TestAggregate.Commands.DeleteCommand(1));

        r.TryGetError(out var error).Should().BeTrue();
        error.Type.Should().Be(ErrorType.Unauthorized);
    }

    private sealed class RequiredNameConfig : IAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
            => b.Command<TestAggregate.Commands.CreateCommand, TestItem>()
                .Validate((cmd, _) => ValueTask.FromResult(cmd.Name.Length > 0
                    ? ValidationResult.Valid
                    : ValidationResult.Of(new Violation(FieldPath.Of("Name"), "required"))));
    }

    private sealed class AuthenticatedDeleteConfig : IAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
            => b.Command<TestAggregate.Commands.DeleteCommand>().RequireAuthenticated();
    }
}
