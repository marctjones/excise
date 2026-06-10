using System.Threading.Tasks;

namespace PdfEditor.Services;

public interface IUserDialogService
{
    Task ShowMessageAsync(string title, string message);
}
