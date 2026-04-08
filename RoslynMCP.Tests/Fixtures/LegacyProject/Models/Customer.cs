using System;
using System.Collections.Generic;

namespace LegacyProject.Models
{
    public class Customer
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Email { get; set; }
        public DateTime CreatedAt { get; set; }

        public List<Order> Orders { get; set; }

        public Customer()
        {
            Orders = new List<Order>();
            CreatedAt = DateTime.UtcNow;
        }

        public string GetDisplayName()
        {
            return string.Format("{0} ({1})", Name, Email);
        }
    }

    public class Order
    {
        public int Id { get; set; }
        public decimal Amount { get; set; }
        public DateTime OrderDate { get; set; }
    }
}
