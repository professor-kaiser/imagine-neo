using System.Collections.ObjectModel;
using PhotoLab.UI.Contracts;
using PhotoLab.UI.Models;

namespace PhotoLab.UI.Services;

public sealed class ImageDocumentService(NamedPipeClient client)
{
    public ObservableCollection<ImageDocument> Documents { get; } = [];
    public async Task RemoveAsync(ImageDocument document)
    {
        await client.SendAsync(new CommandMessage { MessageType = "remove_image", ImageId = document.Id });
        Documents.Remove(document);
    }
}
