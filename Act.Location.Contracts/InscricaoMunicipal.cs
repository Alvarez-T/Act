using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Act.Location.Contracts
{
    public struct InscricaoMunicipal : IEquatable<InscricaoMunicipal>
    {
        private static readonly string RegexPattern = @"[!-ÿ]{1}[ -ÿ]{0,}[!-ÿ]{1}|[!-ÿ]{1}";

        public string Value { get; }

        public InscricaoMunicipal(string inscricao)
        {
            if (string.IsNullOrWhiteSpace(inscricao))
                throw new ArgumentException("Inscrição Municipal não pode ser nula ou vazia.");

            var regex = new Regex(RegexPattern);
            if (!regex.IsMatch(inscricao))
                throw new ArgumentException("Inscrição Municipal inválida.");

            Value = inscricao;
        }

        public override bool Equals(object obj)
        {
            return obj is InscricaoMunicipal other && Equals(other);
        }

        public bool Equals(InscricaoMunicipal other)
        {
            return Value == other.Value;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Value);
        }

        public static bool operator ==(InscricaoMunicipal left, InscricaoMunicipal right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(InscricaoMunicipal left, InscricaoMunicipal right)
        {
            return !(left == right);
        }
    }
}
