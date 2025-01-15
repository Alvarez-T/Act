using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Act.Location.Contracts
{
    public struct InscricaoEstadual : IEquatable<InscricaoEstadual>
    {
        private static readonly Dictionary<string, string> StateRegexMap = new()
    {
        { "01", @"^(01).?\d{3}.?\d{3}/?\d{3}\-?\d{2}$" }, // AC
        { "24", "^(24)(0|3|5|7|8)\\d{6}$" }, // AL
        { "03", "^(03)\\d{7}$" }, // AP
        { "04", @"^\d{2}.?\d{3}.?\d{3}\-?\d{1}$" }, // AM, GO
        { "05", @"^\d{7}\-?\d{2}$" }, // BA (9 dígitos)
        { "06", @"^\d{6}\-?\d{2}$" }, // BA (8 dígitos)
        { "07", @"^\d{8}\-?\d{1}$" }, // CE, PA
        { "08", @"^\d{8}\-?\d{2}$" }, // DF
        { "09", "^\\d{9}$" }, // ES, PI
        { "12", "^(12)\\d{7}$" }, // MA
        { "13", @"^\d{10}\-?\d{1}$" }, // MT
        { "28", "^(28)\\d{7}$" }, // MS
        { "14", @"^\d{3}.?\d{3}.?\d{3}/?\d{4}$" }, // MG
        { "15", @"^(15)\d{6}\-?\d{1}$" }, // PA
        { "16", @"^\d{3}.?\d{5}\-?\d{2}$" }, // PR
        { "17", @"^\d{2}.?\d{3}.?\d{2}\-?\d{1}$" }, // RJ
        { "20", @"^(20).?\d{3}.?\d{3}\-?\d{1}$" }, // RN (9 dígitos)
        { "21", @"^(20).?\d{1}.?\d{3}.?\d{3}\-?\d{1}$" }, // RN (10 dígitos)
        { "22", @"^\d{3}/?\d{7}$" }, // RS
        { "23", @"^\d{3}.?\d{5}\-?\d{1}$" }, // RO (antes de 01/08/2000)
        { "24", @"^\d{13}\-?\d{1}$" }, // RO (a partir de 01/08/2000)
        { "25", @"^(24)\d{6}\-?\d{1}$" }, // RR
        { "26", @"^\d{3}.?\d{3}.?\d{3}$" }, // SC
        { "27", @"^\d{3}.?\d{3}.?\d{3}.?\d{3}$" }, // SP
        { "28", @"^(P)\-?\d{8}.?\d/?\d{3}$" }, // SP (Produtor Rural)
        { "29", @"^\d{8}\-?\d{1}$" }, // SE
        { "30", "^(29)(01|02|03|99)\\d{7}$" } // TO
    };

        public string Value { get; }
        public string State { get; }

        public InscricaoEstadual(string inscricao)
        {
            if (string.IsNullOrWhiteSpace(inscricao))
                throw new ArgumentException("Inscrição Estadual não pode ser nula ou vazia.");

            Value = inscricao;

            // Extract state code (first 2 digits for simplicity in this example)
            var stateCode = inscricao.Substring(0, 2);
            if (!StateRegexMap.ContainsKey(stateCode))
                throw new ArgumentException("Código de estado inválido ou não reconhecido.");

            // Validate against regex
            var regex = new Regex(StateRegexMap[stateCode]);
            if (!regex.IsMatch(inscricao))
                throw new ArgumentException("Inscrição Estadual inválida para o estado especificado.");

            State = stateCode;
        }

        public override bool Equals(object obj)
        {
            return obj is InscricaoEstadual other && Equals(other);
        }

        public bool Equals(InscricaoEstadual other)
        {
            return Value == other.Value && State == other.State;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Value, State);
        }

        public static bool operator ==(InscricaoEstadual left, InscricaoEstadual right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(InscricaoEstadual left, InscricaoEstadual right)
        {
            return !(left == right);
        }
    }
}
