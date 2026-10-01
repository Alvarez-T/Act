using YFex.State;
using YFex.Validation;
using CreateCustomer = YFex.Messaging.Rpc.Tests.ShopAggregate.Commands.CreateCustomer;

namespace YFex.Messaging.Rpc.Tests;

/// <summary>Form over the fluent-only contract: borrows its rules (G8) and maps Document → CpfCnpj.</summary>
[ValidatesAs<CreateCustomer>]
public partial class CustomerFormVm : StateObject
{
    [Observable] public partial string Name { get; set; }
    [Observable] public partial string Email { get; set; }
    [Observable, MapsTo("Document")] public partial string CpfCnpj { get; set; }
}

/// <summary>
/// Phase 3 acceptance (13): <c>CreateCustomer</c> configured ONLY with fluent <c>.Validation(...)</c> → a VM with
/// <c>[ValidatesAs&lt;CreateCustomer&gt;]</c> borrows its rules → the server rejects over RPC → <c>ApplyServer</c> returns
/// <c>Invalid</c> and the violation lands on the mapped VM field.
/// </summary>
public class AcceptanceTests(RpcFixture fx) : IClassFixture<RpcFixture>
{
    [Fact]
    public void ViewModel_borrows_the_fluent_only_contract_rules()
    {
        var vm = new CustomerFormVm { Name = "Ana", Email = "ana@shop.test", CpfCnpj = "12345678901" };
        vm.Validation.HasErrors.Should().BeFalse();

        vm.CpfCnpj = "1";   // Document: Length(11, 14) from ShopBaseline's .Validation(...)

        vm.Validation.Get("CpfCnpj").Contains(RuleCodes.Length).Should().BeTrue();
    }

    [Fact]
    public async Task Server_rejection_over_RPC_lands_on_the_mapped_field()
    {
        // Client-valid, server-invalid: only the server knows the e-mail is taken (context rule, stage 5).
        var command = new CreateCustomer("Ana", EmailNotRegistered.Taken, "12345678901");
        var result = await fx.CreateClientBus().CommandAsync<CreateCustomer, int>(command);

        var vm = new CustomerFormVm { Name = "Ana", Email = EmailNotRegistered.Taken, CpfCnpj = "12345678901" };
        var outcome = vm.ApplyServer<CreateCustomer, int>(result);

        outcome.Should().Be(ServerOutcome.Invalid);
        vm.Validation.Get("Email").Contains("customer.email-taken").Should().BeTrue();
    }

    [Fact]
    public async Task Server_shape_rejection_maps_Document_to_CpfCnpj()
    {
        // An outdated client that skipped validation (drift B): straight to the proxy, typed result back.
        var result = await fx.Contract.CreateCustomerAsync(new CreateCustomer("Ana", "ana@shop.test", "1"));

        var vm = new CustomerFormVm { Name = "Ana", Email = "ana@shop.test", CpfCnpj = "1" };
        vm.ApplyServer<CreateCustomer, int>(result).Should().Be(ServerOutcome.Invalid);

        var onField = vm.Validation.Get("CpfCnpj");
        onField.Contains(RuleCodes.Length).Should().BeTrue();
        onField.AsSpan().ToArray().Should().Contain(v => v.Origin.Source == ViolationSource.Server);
    }

    [Fact]
    public async Task Success_clears_server_violations()
    {
        var vm = new CustomerFormVm { Name = "Ana", Email = EmailNotRegistered.Taken, CpfCnpj = "12345678901" };
        var bus = fx.CreateClientBus();
        var rejected = await bus.CommandAsync<CreateCustomer, int>(new CreateCustomer("Ana", EmailNotRegistered.Taken, "12345678901"));
        var accepted = await bus.CommandAsync<CreateCustomer, int>(new CreateCustomer("Ana", "ana@shop.test", "12345678901"));

        vm.ApplyServer<CreateCustomer, int>(rejected).Should().Be(ServerOutcome.Invalid);
        vm.ApplyServer<CreateCustomer, int>(accepted).Should().Be(ServerOutcome.Ok);
        vm.Validation.HasErrors.Should().BeFalse();
    }
}
