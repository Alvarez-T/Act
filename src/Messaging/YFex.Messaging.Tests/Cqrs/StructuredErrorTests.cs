using YFex.Cqrs;
using YFex.Cqrs.Configuration;
using YFex.Messaging.Tests.Fixtures;
using YFex.Validation;
using Denial = YFex.Authorization.Public.Denial;
using DenialKind = YFex.Authorization.Public.DenialKind;
using AuthorizationVerdict = YFex.Authorization.Public.AuthorizationVerdict;

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

        e.Kind.Should().Be(ErrorKind.None);
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

        e.Kind.Should().Be(ErrorKind.ValidationProblem);
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
        error.Kind.Should().Be(ErrorKind.ValidationProblem);
        error.Violations.Count.Should().Be(1);
        error.Violations[0].Path.ToString().Should().Be("Name");
        error.Violations[0].Code.Value.Should().Be("required");
        TestDispatcherFixture.Store.Should().BeEmpty("the handler must not run");
    }

    [Fact]
    public async Task Anonymous_caller_of_an_authenticated_command_gets_Unauthenticated()
    {
        var fx = new TestDispatcherFixture(configurations: [new AuthenticatedDeleteConfig()]);

        var r = await fx.Dispatcher.CommandAsync(new TestAggregate.Commands.DeleteCommand(1));

        r.TryGetError(out var error).Should().BeTrue();
        error.Kind.Should().Be(ErrorKind.Unauthenticated);
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

    // ── [Union] Error (authorization-consolidation ⚑Z11, Phase 2b) ──────────────────────────────────────────────────────

    [Fact]
    public void Verdicts_map_to_Unauthenticated_and_Forbidden_with_the_denial()
    {
        Error.From(AuthorizationVerdict.Unauthenticated).Kind.Should().Be(ErrorKind.Unauthenticated);

        var denial = new Denial(DenialKind.MissingRole, Code: "Supervisor");
        var forbidden = Error.From(AuthorizationVerdict.Forbidden(denial));
        forbidden.Kind.Should().Be(ErrorKind.Forbidden);
        forbidden.TryGetValue(out Forbidden f).Should().BeTrue();
        f.Denial.Should().Be(denial);
        forbidden.Message.Should().Be("Requires role Supervisor.");

        var allowed = () => Error.From(AuthorizationVerdict.Allowed);
        allowed.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Each_case_answers_only_its_own_TryGetValue()
    {
        Error notFound = new NotFound("gone");
        notFound.TryGetValue(out NotFound n).Should().BeTrue();
        n.Message.Should().Be("gone");
        notFound.TryGetValue(out Failed _).Should().BeFalse();
        notFound.TryGetValue(out Forbidden _).Should().BeFalse();
        notFound.TryGetValue(out ApprovalRequired _).Should().BeFalse();
        notFound.HasViolations.Should().BeFalse();

        Error conflict = new Conflict("stale", ViolationList.From([new Violation(FieldPath.Empty, "rules.decision.mismatch")]));
        conflict.HasViolations.Should().BeTrue();
        conflict.TryGetValue(out Conflict c).Should().BeTrue();
        c.Violations.Count.Should().Be(1);
    }

    [Fact]
    public void Message_override_keeps_the_case_and_a_string_is_a_failure()
    {
        var custom = new Error(new Forbidden(new Denial(DenialKind.ResourceDenied))).WithMessage("Not allowed to publish at t1.");
        custom.Kind.Should().Be(ErrorKind.Forbidden);
        custom.Message.Should().Be("Not allowed to publish at t1.");

        Error plain = "boom";
        plain.Kind.Should().Be(ErrorKind.Failed);
        plain.Message.Should().Be("boom");
    }
}
