// The LINQ to SQL surface Sales.designer.cs needs, declared here because System.Data.Linq does
// not exist on the runtime the fixture compiles against. The Dbml binder matches the mapping
// attributes on their simple names, the way it must for generated code that alias-qualifies them.
namespace System.Data.Linq
{
    public class DataContext { }
    public class Table<T> { }
}

namespace System.Data.Linq.Mapping
{
    public sealed class DatabaseAttribute : System.Attribute { public string? Name { get; set; } }
    public sealed class TableAttribute : System.Attribute { public string? Name { get; set; } }
    public sealed class ColumnAttribute : System.Attribute
    {
        public string? Name { get; set; }
        public string? Storage { get; set; }
        public string? DbType { get; set; }
        public bool IsPrimaryKey { get; set; }
    }
    public sealed class AssociationAttribute : System.Attribute
    {
        public string? Name { get; set; }
        public string? ThisKey { get; set; }
        public string? OtherKey { get; set; }
    }
    public sealed class FunctionAttribute : System.Attribute { public string? Name { get; set; } }
}
