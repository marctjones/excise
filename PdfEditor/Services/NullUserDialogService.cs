using System.Threading.Tasks;

namespace PdfEditor.Services;

public sealed class NullUserDialogService : IUserDialogService
{
    public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
}
