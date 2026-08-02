await tp.Test("The row Location points at is the row we created.", async () =>
{
    dynamic owner = await tp.Response.GetBodyAsExpandoAsync();

    Equal(tp.GetVariable<string>("OwnerId"), (string)owner.id);
});
