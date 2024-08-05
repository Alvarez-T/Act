namespace Act.Location.Contracts;

public record Address(PostalCode PostalCode, string Street, string Number, string District, City City, State State, string Complement);
public record City(CityIBGE CityIBGE, string Name);
public record State(StateIBGE StateIBGE, string Name, string Acronym);