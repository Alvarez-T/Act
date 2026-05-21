namespace YFex.Cqrs;

public class foo
{
    public foo()
    {
        UserDto.Queries.GetUserById(1);
    }
}

public partial class UserDto
{
    public static partial class Queries
    {
        public record GetUserByIdQuery(int Id) : YFex.Cqrs.IQuery<UserDto>;
        public record GetUsersByAgeQuery(int MinAge, int MaxAge) : YFex.Cqrs.IQuery<System.Collections.Generic.List<foo>>;
    }

    public static partial class Commands
    {
        public record CreateUserCommand(string Name, string Email) : YFex.Cqrs.ICommand;
        public record DeleteUserCommand(int Id) : YFex.Cqrs.ICommand;
    }

    public static partial class Events
    {
        public record UserCreatedEvent(int Id, string Name) : YFex.Cqrs.IEvent;
    }
}
