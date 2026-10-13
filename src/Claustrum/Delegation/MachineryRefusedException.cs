using Claustrum.Core.Model;

namespace Claustrum.Delegation;

// #104: a run refused because `.claustrum` leaves the repo. The failed receipt is already on disk; it rides here
// so the CLI can still print it under --json while exiting 2 (a usage refusal, like `init`'s), not the 1 a
// failed status maps to. The MCP doors return the receipt like every other refusal (AsReceiptAsync); the rest
// let the exception boundary say the message.
public sealed class MachineryRefusedException(RunResult receipt) : Exception(receipt.Error)
{
    public RunResult Receipt { get; } = receipt;

    public static async Task<RunResult> AsReceiptAsync(Task<RunResult> run)
    {
        try
        {
            return await run;
        }
        catch (MachineryRefusedException ex)
        {
            return ex.Receipt;
        }
    }
}
