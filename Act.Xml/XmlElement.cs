using System.Diagnostics.CodeAnalysis;
using System.Xml;
using Act.Utilities;

namespace Act.Xml
{
    public class XmlElementAttribute : System.Xml.Serialization.XmlElementAttribute
    {
        [StringSyntax(StringSyntaxAttribute.Regex)] 
        public string DisplayFormat { get; set; }

        public string Length { get; set; }

        /// <devdoc>
        ///    <para>[To be supplied.]</para>
        /// </devdoc>
        public XmlElementAttribute(string? elementName) : base (elementName) { }

        /// <devdoc>
        ///    <para>[To be supplied.]</para>
        /// </devdoc>
        public XmlElementAttribute(Type? type) : base(type) { }

        /// <devdoc>
        ///    <para>[To be supplied.]</para>
        /// </devdoc>
        public XmlElementAttribute(string? elementName, Type? type) : base(elementName, type) { }
    }
}
