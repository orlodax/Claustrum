namespace Claustrum.Core.IO;

// A file a reader must never see half-written (host.json, host-request.json, owner.json): the text goes to a temp file beside it and
// is renamed over the target. Windows refuses a rename while a reader holds the target, so it is tried a few times.
public static class AtomicFile
{
    private const int Attempts = 5;

    public static void WriteAllText(string path, string text)
    {
        string temp = $"{path}.tmp";
        File.WriteAllText(temp, text);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < Attempts)
                {
                    Thread.Sleep(20);
                    continue;
                }

                File.Delete(temp);
                throw;
            }
        }
    }
}
