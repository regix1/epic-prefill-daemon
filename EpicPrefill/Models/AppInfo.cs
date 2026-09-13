namespace EpicPrefill.Models
{
    // TODO comment
    public sealed class AppInfo
    {
        public string AppId { get; set; }
        public string BuildVersion { get; set; }
        public string CatalogItemId { get; set; }
        public string Namespace { get; set; }

        public string Title { get; set; }
        // Null whenever Epic returned no artwork for the app, so every read has to allow for that.
#pragma warning disable CA2227 // This mutable collection is copied between the API and persisted app models.
        public List<KeyImage> KeyImages { get; set; }
#pragma warning restore CA2227

        public override string ToString()
        {
            if (Title == null)
            {
                return $"{AppId} - {Namespace} - {CatalogItemId}";
            }
            return Title;
        }
    }
}
