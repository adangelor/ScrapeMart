namespace ScrapeMart.Entities.dtos
{
    public sealed class RetailerResult
    {
        public string RetailerHost { get; set; } = default!;
        public int StoresProcessed { get; set; }
        public int ProductChecks { get; set; }
        public int AvailableProducts { get; set; }
        public string? ErrorMessage { get; set; }
    }

}
