using MMLib.Alvo.Data;

using static MMLib.Alvo.Tests.Data.TypedFixtures;

namespace MMLib.Alvo.Tests.Data;

/// <summary><c>As&lt;T&gt;()</c> (spec 2026-10-14 §5, HX-05 acceptance (7)): a record as a host type, or an error naming the field.</summary>
public sealed class AlvoRecordAsTests
{
    [Fact]
    public void A_record_fills_every_mapped_property()
    {
        var order = OrderRecord().As<Order>();

        order.Id.ShouldBe(Guid.Parse("0b6f6a7e-4a8e-4bdb-9f3e-0f2b9b6e1c11"));
        order.OwnerId.ShouldBeNull();
        order.Status.ShouldBe(OrderStatus.Approved);
        order.Previous.ShouldBe(OrderStatus.InReview);
        order.Total.ShouldBe(12.5m);
        order.Quantity.ShouldBe(3L);
        order.Rank.ShouldBe(7);
        order.IsPaid.ShouldBeTrue();
        order.Note.ShouldBe("fragile");
        order.DueAt.ShouldBe(new DateTimeOffset(2026, 11, 2, 8, 30, 0, TimeSpan.Zero));
        order.ShipOn.ShouldBe(new DateOnly(2026, 11, 3));
        order.Reference.ShouldBe("R-1");
    }

    [Fact]
    public void A_missing_field_throws_naming_it_and_the_property()
    {
        var failure = Should.Throw<InvalidOperationException>(() => OrderRecord(("note", Absent)).As<Order>());

        failure.Message.ShouldContain("'note'");
        failure.Message.ShouldContain("Order.Note");
    }

    [Fact]
    public void A_null_in_a_property_that_cannot_hold_null_throws_naming_the_field()
    {
        var failure = Should.Throw<InvalidOperationException>(() => OrderRecord(("total", null)).As<Order>());

        failure.Message.ShouldContain("'total'");
        failure.Message.ShouldContain("Order.Total cannot hold null");
    }

    [Fact]
    public void A_whole_decimal_read_back_as_an_integer_fills_a_decimal_property() =>
        OrderRecord(("total", 12L)).As<Order>().Total.ShouldBe(12m);

    [Fact]
    public void An_enum_value_the_type_has_no_member_for_throws_without_echoing_it()
    {
        var failure = Should.Throw<InvalidOperationException>(() => OrderRecord(("status", "archived")).As<Order>());

        failure.Message.ShouldContain("'status'");
        failure.Message.ShouldNotContain("archived");
    }

    [Fact]
    public void A_value_the_property_cannot_take_throws_naming_the_field() =>
        Should.Throw<InvalidOperationException>(() => OrderRecord(("quantity", "three")).As<Order>()).Message.ShouldContain("'quantity'");

    [Fact]
    public void A_positional_record_is_built_through_its_constructor()
    {
        var line = OrderRecord().As<OrderLine>();

        (line.Note, line.Total).ShouldBe(("fragile", 12.5m));
    }

    [Fact]
    public void A_wrapper_keeps_fields_it_does_not_declare()
    {
        var view = OrderRecord(("legacy_code", "L-9")).As<OrderView>();

        view.Note.ShouldBe("fragile");
        view.Record["legacy_code"].ShouldBe("L-9");
    }

    [Fact]
    public void A_wrapper_missing_a_field_it_declares_throws_naming_it() =>
        Should.Throw<InvalidOperationException>(() => OrderRecord(("note", Absent)).As<OrderView>()).Message.ShouldContain("'note'");

    [Fact]
    public void A_setter_s_own_exception_surfaces_unwrapped() =>
        Should.Throw<ArgumentException>(() => OrderRecord().As<RefusingNote>()).Message.ShouldBe("The host refuses this note.");

    [Fact]
    public void A_wrapper_constructor_s_own_exception_surfaces_unwrapped() =>
        Should.Throw<ArgumentException>(() => OrderRecord().As<RefusingView>()).Message.ShouldBe("The host refuses this record.");

    [Fact]
    public void A_value_the_property_cannot_take_names_its_type_with_its_article() =>
        Should.Throw<InvalidOperationException>(() => OrderRecord(("note", 3L)).As<Order>()).Message
            .ShouldBe("Field 'note' holds an Int64, which Order.Note (String) cannot take.");

    [Fact]
    public void A_type_with_a_shape_problem_cannot_be_read() =>
        Should.Throw<InvalidOperationException>(() => AlvoRecord.Empty.As<TicketWithScore>()).Message.ShouldContain("Double");
}
