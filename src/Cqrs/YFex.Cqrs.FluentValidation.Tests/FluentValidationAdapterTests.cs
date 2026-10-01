using FluentAssertions;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using YFex.Cqrs;
using YFex.Cqrs.Configuration;
using YFex.Cqrs.FluentValidation;
using YFex.Cqrs.Runtime;

namespace YFex.Cqrs.FluentValidation.Tests;

public sealed partial class Crm
{
    public static partial class Commands
    {
        public sealed record RegisterLeadCommand(string Name, int Score) : ICommand<int>;
        public sealed record ArchiveLeadCommand(string Name) : ICommand;
    }

    public static partial class Queries
    {
        public sealed record FindLeadsQuery(string Term) : IQuery<string[]>;
    }
}

public sealed class LeadValidator : AbstractValidator<Crm.Commands.RegisterLeadCommand>
{
    public LeadValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithErrorCode("lead.name");
        RuleFor(x => x.Score).InclusiveBetween(0, 100).WithSeverity(Severity.Warning);
    }
}

public sealed class ArchiveValidator : AbstractValidator<Crm.Commands.ArchiveLeadCommand>
{
    public ArchiveValidator() => RuleFor(x => x.Name).NotEmpty();
}

public sealed class FindValidator : AbstractValidator<Crm.Queries.FindLeadsQuery>
{
    public FindValidator() => RuleFor(x => x.Term).MinimumLength(3);
}

public sealed class CrmConfig : IAggregateConfiguration<Crm>
{
    public void Configure(AggregateConfigurationBuilder<Crm> b)
    {
        b.Command<Crm.Commands.RegisterLeadCommand, int>().UseFluentValidator(new LeadValidator());
        b.Command<Crm.Commands.ArchiveLeadCommand>().UseFluentValidator(new ArchiveValidator());
        b.Query<Crm.Queries.FindLeadsQuery, string[]>().UseFluentValidator(new FindValidator());
    }
}

public class FluentValidationAdapterTests
{
    private static readonly IServiceProvider Services = new ServiceCollection().BuildServiceProvider();
    private static readonly CompiledMessagingRegistry Registry = CompiledMessagingRegistry.Build([new CrmConfig()]);

    private static Task<YFex.Validation.ValidationResult> Run<T>(T message, MessageValidator? v)
        => MessagePipeline.CheckContextAsync(message, v, Services, default).AsTask();

    [Fact]
    public async Task Failures_become_violations_with_code_path_severity_and_detail()
    {
        var v = Registry.Commands[typeof(Crm.Commands.RegisterLeadCommand)].Validation;

        var r = await Run(new Crm.Commands.RegisterLeadCommand("", 500), v);

        r.IsValid.Should().BeFalse();
        r.Violations.Count.Should().Be(2);
        r.Violations[0].Path.ToString().Should().Be("Name");
        r.Violations[0].Code.Value.Should().Be("lead.name");
        r.Violations[0].Detail.Should().NotBeNullOrEmpty();
        r.Violations[1].Severity.Should().Be(YFex.Validation.Severity.Warning);
        r.Violations[1].Code.Value.Should().Be("InclusiveBetweenValidator", "FluentValidation's own error code is kept");
    }

    [Fact]
    public async Task Void_commands_and_queries_are_supported()
    {
        (await Run(new Crm.Commands.ArchiveLeadCommand(""), Registry.Commands[typeof(Crm.Commands.ArchiveLeadCommand)].Validation))
            .IsValid.Should().BeFalse();
        (await Run(new Crm.Queries.FindLeadsQuery("ab"), Registry.Queries[typeof(Crm.Queries.FindLeadsQuery)].Validation))
            .Violations[0].Path.ToString().Should().Be("Term");
        (await Run(new Crm.Queries.FindLeadsQuery("abc"), Registry.Queries[typeof(Crm.Queries.FindLeadsQuery)].Validation))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void YFex_Cqrs_does_not_reference_FluentValidation()
        => typeof(ICommand).Assembly.GetReferencedAssemblies().Select(a => a.Name)
            .Should().NotContain("FluentValidation");
}
