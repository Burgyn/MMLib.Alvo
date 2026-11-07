using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Extensions;
using MMLib.Alvo.Functions;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The field-service world with a host function, <c>issue-invoice</c>, registered as a host developer would with
/// <c>AddFunction</c>, and one automation rule calling it when a region is created (HX-15).
/// </summary>
/// <remarks>Its own world: the function's deliveries would otherwise join the lists other worlds' scenarios read first.</remarks>
public sealed class FunctionHookWorld : AdminWorld
{
    internal const string IssueInvoice = "issue-invoice";

    internal const string Rule = "invoice-new-region";

    /// <summary>The codes the function was called with, in call order.</summary>
    internal ConcurrentQueue<string> Calls { get; } = new();

    /// <inheritdoc/>
    protected override string Descriptor
    {
        get
        {
            var descriptor = JsonNode.Parse(Descriptors.FieldService)!.AsObject();
            descriptor["automation"] = JsonNode.Parse($$"""
                {
                  "{{Rule}}": {
                    "description": "Invoices the setup of every new region.",
                    "trigger": { "event": "entity.regions.created" },
                    "actions": [ { "type": "function", "name": "{{IssueInvoice}}", "input": { "code": { "$cel": "new.code" }, "note": null } } ]
                  }
                }
                """);
            return descriptor.ToJsonString();
        }
    }

    /// <inheritdoc/>
    protected override void Configure(IAlvoBuilder alvo) => alvo.AddFunction<InvoiceFunction>(IssueInvoice);

    /// <inheritdoc/>
    protected override void Configure(IServiceCollection services) => services.AddSingleton(this);
}

/// <summary>The input <c>issue-invoice</c> takes: a required code and an optional note.</summary>
/// <param name="Code">The region code to invoice.</param>
/// <param name="Note">A note for the invoice, or <see langword="null"/>.</param>
public sealed record InvoiceInput(string Code, string? Note);

/// <summary>Records each call on its world.</summary>
/// <param name="world">The world the calls are recorded on.</param>
public sealed class InvoiceFunction(FunctionHookWorld world) : IAlvoFunction<InvoiceInput>
{
    public Task InvokeAsync(InvoiceInput input, AlvoFunctionContext context, CancellationToken cancellationToken)
    {
        world.Calls.Enqueue(input.Code);
        return Task.CompletedTask;
    }
}
