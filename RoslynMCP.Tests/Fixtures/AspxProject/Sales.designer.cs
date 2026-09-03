// The shape SqlMetal writes for Sales.dbml, by hand: the attributes are the anchors the Dbml
// binder matches against the model, so their names and arguments follow the real tool's output.
namespace AspxProject
{
    using System.Data.Linq;
    using System.Data.Linq.Mapping;

    [Database(Name = "Sales")]
    public partial class SalesDataContext : DataContext
    {
        public Table<Invoice> Invoices { get { return null!; } }
    }

    [Table(Name = "dbo.Invoices")]
    public partial class Invoice
    {
        [Column(Name = "Id", Storage = "_Id", IsPrimaryKey = true)]
        public int Id { get; set; }

        [Column(Name = "Reference", Storage = "_Reference")]
        public string Reference { get; set; } = "";
    }
}
