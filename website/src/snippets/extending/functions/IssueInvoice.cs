using MMLib.Alvo.Functions;

namespace Billing;

/// <summary>What the descriptor passes: its <c>input</c> is checked against this type's JSON Schema at apply.</summary>
public sealed record InvoiceInput(string OrderNumber, decimal Amount, string Currency);

/// <summary>Issues one invoice per order; registered with <c>AddFunction&lt;IssueInvoice&gt;("issue-invoice")</c>.</summary>
public sealed class IssueInvoice(IInvoicing invoicing) : IAlvoFunction<InvoiceInput>
{
    public async Task InvokeAsync(InvoiceInput input, AlvoFunctionContext context, CancellationToken cancellationToken)
    {
        if (input.Amount == 0m)
        {
            throw new AlvoFunctionSkippedException("Nothing to invoice.");
        }

        // At least once: the event id is the idempotency key the invoicing system dedupes on.
        await invoicing.IssueAsync(context.EventId, input.OrderNumber, input.Amount, input.Currency, cancellationToken);
    }
}

public interface IInvoicing
{
    Task IssueAsync(Guid idempotencyKey, string orderNumber, decimal amount, string currency, CancellationToken cancellationToken);
}
