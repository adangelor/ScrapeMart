namespace ScrapeMart.Entities.dtos
{
    public sealed class ComprehensiveResult
    {
        public bool Success { get; set; }
        public int TotalRetailers { get; set; }
        public int TotalProductsToTrack { get; set; }
        public int TotalStoresProcessed { get; set; }
        public int TotalProductChecks { get; set; }
        public int TotalAvailableProducts { get; set; }
        public Dictionary<string, RetailerResult> RetailerResults { get; set; } = new();
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public TimeSpan Duration => CompletedAt.HasValue ? CompletedAt.Value - StartedAt : TimeSpan.Zero;
        public string? ErrorMessage { get; set; }
    }

}
