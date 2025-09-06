namespace ScrapeMart.Entities
{
    public sealed class VtexOptions
    {
        public string AccountName { get; set; } = default!;
        public string Environment { get; set; } = "vtexcommercestable.com.br";
        public int CategoryTreeDepth { get; set; } = 50;
        public int PageSize { get; set; } = 50;
        public string BaseHost => $"{AccountName}.{Environment}";
        public Uri BaseUri => new($"https://{BaseHost}/");
    }
}
