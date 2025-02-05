namespace Act.Location.Contracts;

public record Address(CEP Cep, string Street, string Number, string District, City City, State State, string Complement);
public record City(MunicipioIBGE CidadeIbge, string Name);
public record State(StateIBGE StateIBGE, string Name, string Acronym);