using Square.Models;

namespace invoicer.Models
{
    public class BookingInvoice
    {
        public Booking Booking { get; set; }
        public Order Order { get; set; }
    }
}