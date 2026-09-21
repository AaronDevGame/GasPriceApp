using System;
using System.Collections.Generic;

public enum MockFuelType
{
    Diesel,
    Regular91,
    Premium95
}

[Serializable]
public sealed class MockCityFuelPrice
{
    public string city;
    public double latitude;
    public double longitude;
    public float diesel;
    public float regular91;
    public float premium95;

    public float GetPrice(MockFuelType fuelType) => fuelType switch
    {
        MockFuelType.Diesel => diesel,
        MockFuelType.Regular91 => regular91,
        MockFuelType.Premium95 => premium95,
        _ => diesel
    };
}

public static class MockFuelPriceData
{
    public static readonly IReadOnlyList<MockCityFuelPrice> Cities = new[]
    {
        City("Manila", 14.5995, 120.9842, 61.35f, 65.20f, 72.10f),
        City("Quezon City", 14.6760, 121.0437, 60.90f, 64.75f, 71.55f),
        City("Baguio", 16.4023, 120.5960, 63.40f, 67.10f, 73.80f),
        City("Angeles", 15.1450, 120.5887, 59.85f, 63.90f, 70.45f),
        City("Batangas", 13.7565, 121.0583, 60.25f, 64.35f, 71.20f),
        City("Naga", 13.6218, 123.1948, 62.60f, 66.80f, 73.45f),
        City("Iloilo", 10.7202, 122.5621, 58.95f, 63.25f, 69.90f),
        City("Bacolod", 10.6765, 122.9509, 59.40f, 63.70f, 70.15f),
        City("Cebu", 10.3157, 123.8854, 61.75f, 65.95f, 72.80f),
        City("Tacloban", 11.2543, 124.9617, 63.10f, 67.35f, 74.05f),
        City("Cagayan de Oro", 8.4542, 124.6319, 60.55f, 64.60f, 71.35f),
        City("Davao", 7.1907, 125.4553, 58.60f, 62.85f, 69.55f),
        City("General Santos", 6.1164, 125.1716, 59.15f, 63.15f, 69.85f),
        City("Zamboanga", 6.9214, 122.0790, 62.15f, 66.25f, 72.95f)
    };

    private static MockCityFuelPrice City(
        string name,
        double latitude,
        double longitude,
        float diesel,
        float regular91,
        float premium95) => new()
    {
        city = name,
        latitude = latitude,
        longitude = longitude,
        diesel = diesel,
        regular91 = regular91,
        premium95 = premium95
    };
}
