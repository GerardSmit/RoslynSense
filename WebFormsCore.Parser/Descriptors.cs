#pragma warning disable RS2000
#pragma warning disable RS2008

using Microsoft.CodeAnalysis;

namespace WebFormsCore;

public static class Descriptors
{
    private const string Category = "WebFormsCore";

    public static readonly DiagnosticDescriptor SourceGeneratorException = new("WFC0001", "Source generator exception", "An exception occurred in the source generator: {0}", Category, DiagnosticSeverity.Error, true);
    public static readonly DiagnosticDescriptor PropertyNotFound = new("WFC0002", "Could not find property", "Could not find property '{0}' on type '{1}'", Category, DiagnosticSeverity.Warning, true);
    public static readonly DiagnosticDescriptor DuplicateControlRegister = new("WFC0003", "Duplicate control register", "Duplicate control register for tag prefix '{0}' and tag name '{1}'", Category, DiagnosticSeverity.Warning, true);
    public static readonly DiagnosticDescriptor ControlNotFound = new("WFC0004", "Control not found", "Could not find control '{0}'", Category, DiagnosticSeverity.Warning, true);
    public static readonly DiagnosticDescriptor InheritNotFound = new("WFC0005", "Inherit not found", "Could not detect the inherit attribute in file '{0}'", Category, DiagnosticSeverity.Warning, true);
    public static readonly DiagnosticDescriptor UnexpectedClosingTag = new("WFC0006", "Unexpected closing tag", "Expected closing tag for '{0}' but found '{1}'", Category, DiagnosticSeverity.Warning, true);
    public static readonly DiagnosticDescriptor TypeNotFoundInNamespace = new("WFC0007", "Type not found in namespace", "Could not find type '{0}' in namespace '{1}'", Category, DiagnosticSeverity.Warning, true);
    public static readonly DiagnosticDescriptor EventHandlerNotFound = new("WFC0008", "Event handler not found", "'{1}' does not contain a definition for '{0}'", Category, DiagnosticSeverity.Warning, true);
    public static readonly DiagnosticDescriptor DuplicateAttribute = new("WFC0009", "Duplicate attribute", "The attribute '{0}' is written more than once on this tag; the first is used", Category, DiagnosticSeverity.Warning, true);
    public static readonly DiagnosticDescriptor InheritsTypeNotFound = new("WFC0010", "Code-behind class not found", "Could not find the class '{0}' named by Inherits; the page is read as '{1}'", Category, DiagnosticSeverity.Warning, true);
    public static readonly DiagnosticDescriptor ClosingTagWithNothingOpen = new("WFC0011", "Closing tag with nothing open", "Unexpected closing tag '{0}': nothing is open here", Category, DiagnosticSeverity.Warning, true);
}
