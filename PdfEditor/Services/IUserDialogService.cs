using System.Threading.Tasks;

namespace PdfEditor.Services;

public interface IUserDialogService
{
    Task ShowMessageAsync(string title, string message);

    Task<string?> PromptTextAsync(string title, string message, string? defaultValue = null) =>
        Task.FromResult<string?>(defaultValue);
}
