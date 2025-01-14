namespace Act.Location.Contracts;

public record Address(CEP Cep, string Street, string Number, string District, City City, State State, string Complement);
public record City(CidadeIBGE CidadeIbge, string Name);
public record State(StateIBGE StateIBGE, string Name, string Acronym);