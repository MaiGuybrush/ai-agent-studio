using System;
using System.Collections.Generic;
using System.Linq;

namespace AiAgentStudio.Api.Models;
public enum ShopEnvironmentType
{
    Production = 1,
    Test = 2
}

public enum ShopCategory
{
    UNKNOWN,
    TFT,
    CF,
    LCD,
    MOD,
    PLP
}

public class Shop
{
    public static Shop[] Shops = new Shop[]
    {
        new Shop{ ID = "TS1", Fab = "FABTS1", DllShopName = "Ts1", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.PLP},
        new Shop{ ID = "TFT1", Fab = "FAB1", DllShopName = "Tft1", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.TFT},
        new Shop{ ID = "CF1", Fab = "FAB1", DllShopName = "Cf1", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.CF },
        new Shop{ ID = "LCD1", Fab = "FAB1", DllShopName = "Lcd1", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.LCD },
        new Shop{ ID = "TFT2", Fab = "FAB2", DllShopName = "Tft2", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.TFT },
        new Shop{ ID = "CF2", Fab = "FAB2", DllShopName = "Cf2", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.CF },
        new Shop{ ID = "LCD2", Fab = "FAB2", DllShopName = "Lcd2", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.LCD },
        new Shop{ ID = "TFT3", Fab = "FAB3", DllShopName = "Tft3", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.TFT },
        new Shop{ ID = "CF3", Fab = "FAB3", DllShopName = "Cf3", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.CF },
        new Shop{ ID = "LCD3", Fab = "FAB3", DllShopName = "Lcd3", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.LCD },
        new Shop{ ID = "TFT4", Fab = "FAB4", DllShopName = "Tft4", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.TFT },
        new Shop{ ID = "CF4", Fab = "FAB4", DllShopName = "Cf4", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.CF },
        new Shop{ ID = "LCD4", Fab = "FAB4", DllShopName = "Lcd4", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.LCD },
        new Shop{ ID = "TFT5", Fab = "FAB5", DllShopName = "Tft5", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.TFT },
        new Shop{ ID = "CF5", Fab = "FAB5", DllShopName = "Cf5", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.CF },
        new Shop{ ID = "LCD5", Fab = "FAB5", DllShopName = "Lcd5", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.LCD },
        new Shop{ ID = "TFT6", Fab = "FAB6", DllShopName = "Tft6", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.TFT },
        new Shop{ ID = "CF6", Fab = "FAB6", DllShopName = "Cf6", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.CF },
        new Shop{ ID = "LCD6", Fab = "FAB6", DllShopName = "Lcd6", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.LCD },
        new Shop{ ID = "TFT7", Fab = "FAB7", DllShopName = "Tft7", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.TFT },
        new Shop{ ID = "CF7", Fab = "FAB7", DllShopName = "Cf7", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.CF },
        new Shop{ ID = "LCD7", Fab = "FAB7", DllShopName = "Lcd7", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.LCD },
        new Shop{ ID = "TFT8", Fab = "FAB8", DllShopName = "Tft8", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.TFT },
        new Shop{ ID = "CF8", Fab = "FAB8", DllShopName = "Cf8", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.CF },
        new Shop{ ID = "LCD8", Fab = "FAB8", DllShopName = "Lcd8", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.LCD },
        new Shop{ ID = "TFT8B", Fab = "FAB8B", DllShopName = "Tft8b", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.TFT },
        new Shop{ ID = "CF8B", Fab = "FAB8B", DllShopName = "Cf8b", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.CF },
        new Shop{ ID = "TFTT6", Fab = "FABT6", DllShopName = "Tftl", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.TFT },
        new Shop{ ID = "CFT6", Fab = "FABT6", DllShopName = "Cfl", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.CF },
        new Shop{ ID = "LCDT6", Fab = "FABT6", DllShopName = "Lcdl", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.LCD },
        new Shop{ ID = "USL", Fab = "LCM", DllShopName = "Usl", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.LCD },
        new Shop{ ID = "USLB", Fab = "LCM", DllShopName = "Usl", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.LCD },
        new Shop{ ID = "MOD1", Fab = "LCM", DllShopName = "Mod", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.MOD },
        new Shop{ ID = "MOD2", Fab = "LCM", DllShopName = "Mod", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.MOD },
        new Shop{ ID = "MOD3", Fab = "LCM", DllShopName = "Mod", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.MOD },
        new Shop{ ID = "MOD4", Fab = "LCM", DllShopName = "Mod", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.MOD },
        new Shop{ ID = "SMT1", Fab = "LCM", DllShopName = "Mod", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.MOD },
        new Shop{ ID = "FCT1", Fab = "LCM", DllShopName = "Mod", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.MOD },
        new Shop{ ID = "WIS1", Fab = "LCM", DllShopName = "Mod", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.MOD },
        new Shop{ ID = "N9", Fab = "LCM", DllShopName = "Mod", EnvironmentType = ShopEnvironmentType.Production, Category = ShopCategory.MOD },            
    };

    public static List<String> GetFabs()
    {
        return Shops.Select(m => m.Fab).Distinct().ToList();
    }

    public static Shop GetShop(string id)
    {
        List<Shop> shops = Shops.Where(m => string.Compare(m.ID, id, true) == 0).ToList();
        return shops.Count > 0 ? shops[0] : null;
    }

    public static List<Shop> GetByCategory(ShopEnvironmentType environmentType, ShopCategory category)
    {
        return Shops.Where(m => m.Category == category && m.EnvironmentType == environmentType).ToList();
    }

    public static List<Shop> GetShops(bool productionOnly)
    {
        List<Shop> output = new List<Shop>();
        foreach (var shop in Shops)
        {
            if (shop.EnvironmentType != ShopEnvironmentType.Production && productionOnly)
            {
                continue;
            }
            output.Add(shop);
        }
        return output;
    }

    public static List<Shop> GetShops(ShopEnvironmentType environmentType, String Fab = "")
    {
        List<Shop> output;
        /*foreach (Shop shop in Shops)
        {
            if (shop.EnvironmentType == environmentType)
            {
                output.Add(shop);
            }
        }*/

        if (!string.IsNullOrWhiteSpace(Fab))
        {
            output = Shops.Where(m => m.EnvironmentType == environmentType && m.Fab == Fab.ToUpper()).ToList();
        }
        else
        {
            output = Shops.Where(m => m.EnvironmentType == environmentType).ToList();
        }
        return output;
    }

    public static string GetFab(string shop)
    {
        return Shops.Where(mShop => string.Compare(mShop.ID, shop, true) == 0)
            .Select(m => m.Fab).FirstOrDefault();
    }

    public static String[] DllShops
    {
        get
        {
            return Shops.Where(m => !string.IsNullOrWhiteSpace(m.DllShopName)).Select(m => m.DllShopName).Distinct().ToArray();
        }
    }

    public ShopEnvironmentType EnvironmentType
    {
        get;
        set;
    }
    public string ID 
    {
        get;
        set;
    }
    public string Fab
    {
        get;
        set;
    }
    public string DllShopName
    {
        get;
        set;
    }

    public ShopCategory Category
    {
        get;
        set;
    }

    public static string GetShopCategoryString(string shop)
    {

        string shopUp = shop.ToUpper();

        Shop shopObj = GetShop(shopUp);

        return Enum.GetName(typeof(ShopCategory), shopObj.Category);
    }

    public static ShopCategory GetShopCategory(string shop)
    {
        //string shopUp = shop.ToUpper();
        //Dictionary<ShopCategory, Regex> shopNamingMap = new Dictionary<ShopCategory, Regex>() 
        //                            {
        //                                {ShopCategory.TFT, new Regex("^TFT[1-9L].*")},
        //                                {ShopCategory.CF, new Regex("^CF[1-9L].*")},
        //                                {ShopCategory.LCD, new Regex("^LCD[1-9L].*|^USL.*")}
        //                            };
        //foreach (ShopCategory key in shopNamingMap.Keys)
        //{
        //    if (shopNamingMap[key].IsMatch(shopUp))
        //    {
        //        return key;
        //    }
        //}
        return Shop.GetShop(shop).Category;
//            return ShopCategory.UNKNOWN;
    }

    public string GetDescription(){
        string description = "";
        string shop = ID;
        switch (EnvironmentType) 
        {
            case ShopEnvironmentType.Production:
                description = "Production";
                break;
            case ShopEnvironmentType.Test:
                description = "Testing";
                shop = ID.Substring(0, ID.Length - 1);
                break;
            default:
                description = "Unknown";
                break;
        }
        return  shop + " " + description;

    }

    public static Dictionary<string, Shop> GetShopMap(bool productionOnly)
    {
        Dictionary<string, Shop> output = new Dictionary<string,Shop>();
        foreach (var shop in Shops)
        {
            if (shop.EnvironmentType != ShopEnvironmentType.Production && productionOnly)
            {
                continue;
            }
            output.Add(shop.ID, shop);
        }
        return output;
    }
}

