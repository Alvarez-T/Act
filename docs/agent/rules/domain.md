# Rules for domain projects

These rules add to [all-projects](all-projects.md).

## Models and names

| Kind | Name | When |
|---|---|---|
| Public contract | the concept itself: `Customer` (never `Dto`) | Always. It is the shape crossing module and process boundaries, and it hosts the module's queries, commands and events. |
| UI model | suffix `Model`: `CustomerListModel`, `CustomerEditModel` | One per screen shape. A concept usually has several. |
| Entity | suffix `Entity`: `CustomerEntity` | An exact 1:1 copy of a table, required with EF Core. With YFex.Data, map rows straight to the contract when the shapes match; add an Entity only when they differ. |
| Domain model | suffix `Domain`: `CustomerDomain`, `SaleDomain` | Only when one operation must hold and change in-memory state inside its module. Otherwise the command handler maps the command to storage and persists it. |
| Saga / process manager | suffix `Saga` / `Process` (YFex.Orchestration) | A workflow that spans modules or time: correlated steps, waits for events, timeouts, compensation. Never a domain model. |

`Session`, `Process`, `Saga`, `State` and `Aggregate` are taken by other libraries (Authentication/Offline sessions, Orchestration, YFex.State, `IAggregateConfiguration<T>`). Don't use them as domain-model suffixes.

## Contracts and CQRS

Modules talk to each other only through queries, commands and events declared on the public contract. Every container and record is `partial` (the YFex.Cqrs generator emits `Customer.Queries.GetById(id)`-style helpers):

```csharp
public partial record Customer(CustomerId Id, PartyName Name, CpfCnpj CpfCnpj, Email Email)
{
    public static partial class Queries
    {
        public partial record GetByIdQuery(CustomerId Id) : IQuery<Customer>;
        public partial record ListQuery(int Page, int PageSize) : IQuery<PagedList<Customer>>;
    }
    public static partial class Commands
    {
        public partial record CreateCommand(PartyName Name, CpfCnpj CpfCnpj, Email Email) : ICommand<CustomerId>;
    }
    public static partial class Events
    {
        public partial record Created(CustomerId Id, PartyName Name) : IEvent;
    }
}
```

- Record names drop the container's name: `GetByIdQuery`, not `GetCustomerByIdQuery`. The helper name is the record name without `Query` / `Command`.
- Parameters are value objects, never raw `string` or `int`.
- Behaviour goes in one `IAggregateConfiguration<T>` per contract, never on handlers or as extra attributes: validation, authorization, caching, invalidation, idempotency, offline queueing. Marker interfaces (`IQueueable`, `ICacheable`) are the only exception. Caller guide: [YFex.Cqrs](../../public/cqrs.md); validation: [validation](../../internal/validation/README.md). Check current symbols with the RoslynMcp tools before copying an example.

## Structure and style

- Group code by module, then by feature or use case (vertical slices). Business values are welcome here.
- Use the framework instead of rebuilding it: YFex.Validation and YFex.Rules for validation and rules, YFex.Data for SQL, and the rest of the owners in [ecosystem-fit](ecosystem-fit.md). Domain code depends on infrastructure, never the reverse. Logic you are writing that has no business meaning is a placement decision for the user: name the infrastructure library it would fit.
- Readability first: LINQ, records and the `union` declaration are fine. Apply the infrastructure performance rules only to a measured hot path.
