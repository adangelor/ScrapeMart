namespace ScrapeMart.Entities.dtos;

public record PriceQueryResponseDto(
    string Ean,
    string ProductName,
    string StoreName,
    string RetailerName,
    List<PriceOfferDto> Prices
);

public record PriceOfferDto(
    string SellerId,
    string SellerName,
    decimal Price,
    decimal? ListPrice,
    bool IsAvailable,
    int AvailableQuantity,
    DateTime CapturedAt
);