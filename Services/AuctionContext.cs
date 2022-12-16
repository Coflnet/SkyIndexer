using Microsoft.EntityFrameworkCore;
using Coflnet.Sky.Core;
using MessagePack;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;

namespace Coflnet.Sky.Indexer;
public class AuctionContext : DbContext
{
    public DbSet<SaveAuction> Auctions { get; set; }

    public DbSet<SaveBids> Bids { get; set; }

    public DbSet<Player> Players { get; set; }

    public DbSet<SubscribeItem> SubscribeItem { get; set; }
    public DbSet<DBItem> Items { get; set; }
    public DbSet<AlternativeName> AltItemNames { get; set; }

    public DbSet<AveragePrice> Prices { get; set; }
    public DbSet<Enchantment> Enchantment { get; set; }

    public DbSet<GoogleUser> Users { get; set; }
    public DbSet<NBTLookup> NBTLookups { get; set; }
    public DbSet<NBTKey> NBTKeys { get; set; }
    public DbSet<NBTValue> NBTValues { get; set; }
    public DbSet<Bonus> Boni { get; set; }

    public static string DbContextId = SimplerConfig.Config.Instance["DBConnection"];
    public static string DBVersion = SimplerConfig.Config.Instance["DBVersion"] ?? "10.3";

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseMySql(DbContextId, new MariaDbServerVersion(DBVersion),
        opts => opts.CommandTimeout(60).MaxBatchSize(100)).EnableSensitiveDataLogging();
    }



    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);


        modelBuilder.Entity<Auction>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.End);
            entity.HasIndex(e => e.SellerId);
            entity.HasIndex(e => new { e.ItemId, e.End });
            entity.HasMany(e => e.NBTLookup).WithOne().HasForeignKey("AuctionId");
            entity.HasIndex(e => e.UId).IsUnique();
            //entity.HasOne<NbtData>(d=>d.NbtData);
            //entity.HasMany<Enchantment>(e=>e.Enchantments);
        });

        modelBuilder.Entity<Bid>(entity =>
        {
            entity.HasIndex(e => e.BidderId);
        });

        modelBuilder.Entity<NbtData>(entity =>
        {
            entity.HasKey(e => e.Id);
        });

        modelBuilder.Entity<Player>(entity =>
        {
            entity.HasIndex(e => e.UuId).IsUnique();
            entity.HasIndex(e => e.Name);
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UId);
        });

        modelBuilder.Entity<AveragePrice>(entity =>
        {
            entity.HasIndex(e => new { e.ItemId, e.Date }).IsUnique();
        });

        modelBuilder.Entity<Enchantment>(entity =>
        {
            entity.HasIndex(e => new { e.ItemType, e.Type, e.Level });
        });

        modelBuilder.Entity<NBTLookup>(entity =>
        {
            entity.HasKey(e => new { e.AuctionId, e.KeyId });
            entity.HasIndex(e => new { e.KeyId, e.Value });
        });

        modelBuilder.Entity<NBTKey>(entity =>
        {
            entity.HasIndex(e => e.Slug);
        });
    }
}

public class Auction
{
    [IgnoreMember]
    [JsonIgnore]
    public long Id { get; set; }
    [Key(0)]
    //[JsonIgnore]
    [JsonProperty("uuid")]
    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "char(32)")]
    public string Uuid { get; set; }

    [Key(3)]
    [JsonProperty("startingBid")]
    public long StartingBid { get; set; }

    private string _tag;
    [Key(6)]
    [System.ComponentModel.DataAnnotations.MaxLength(40)]
    [JsonProperty("tag")]
    public string Tag
    {
        get
        {
            return _tag;
        }
        set
        {
            _tag = value.Truncate(40);
        }
    }

    private string _itemName;
    [Key(8)]
    [System.ComponentModel.DataAnnotations.MaxLength(45)]
    [MySqlCharSet("utf8")]
    [JsonProperty("itemName")]
    public string ItemName { get { return _itemName; } set { _itemName = value?.Substring(0, Math.Min(value.Length, 45)); } }

    [Key(9)]
    [JsonProperty("start")]
    public DateTime Start { get; set; }

    [Key(10)]
    [JsonProperty("end")]
    public DateTime End { get; set; }

    [Key(11)]
    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "char(32)")]
    [JsonProperty("auctioneerId")]
    public Guid AuctioneerId { get; set; }

    [Key(12)]
    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "char(32)")]
    [JsonProperty("profileId")]
    public Guid ProfileId { get; set; }

    [IgnoreMember]
    public List<CoopMember> CoopMembers { get; set; }

    [Key(15)]
    [JsonProperty("highestBidAmount")]
    public long HighestBidAmount { get; set; }

    [Key(16)]
    [JsonProperty("bids")]
    public List<SaveBids> Bids { get; set; }

    [Key(18)]
    [JsonProperty("enchantments")]
    public List<AuctionEnchantment> Enchantments { get; set; }

    [Key(19)]
    [JsonProperty("nbtData")]
    public NbtData NbtData { get; set; }
    [Key(20)]
    [JsonProperty("itemCreatedAt")]
    public DateTime ItemCreatedAt { get; set; }
    [Key(21)]
    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "TINYINT(2)")]
    [JsonConverter(typeof(StringEnumConverter))]
    [JsonProperty("reforge")]
    public ItemReferences.Reforge Reforge { get; set; }

    [JsonConverter(typeof(StringEnumConverter))]
    [Key(23)]
    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "TINYINT(2)")]
    [JsonProperty("tier")]
    public Tier Tier { get; set; }
    [Key(24)]
    [JsonProperty("bin")]
    public bool Bin { get; set; }
    [Key(25)]
    [JsonIgnore]
    public int SellerId { get; set; }
    [IgnoreMember]
    [JsonIgnore]
    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "MEDIUMINT(9)")]
    public int ItemId { get; set; }
    [IgnoreMember]
    [JsonIgnore]
    public List<NBTLookup> NBTLookup { get; set; }

    /// <summary>
    /// The first part of a uuid converted to a long for faster lookups
    /// </summary>
    [Key(26)]
    [JsonIgnore]
    public long UId { get; set; }
}

public class CoopMember
{
    public Auction Auction { get; set; }
    public Guid AccountId { get; set; }
}

public class AuctionEnchantment
{
    public Auction Auction { get; set; }
    public Enchantment.EnchantmentType Type { get; set; }
    public byte Level { get; set; }
}

public class Bid
{
    [IgnoreMember]
    [JsonIgnore]
    public int Id { get; set; }
    [IgnoreMember]
    [JsonIgnore]
    public Auction Auction { get; set; }
    [Key(1)]
    [JsonProperty("bidder")]
    public Guid Bidder { get; set; }

    [Key(2)]
    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "char(32)")]
    [JsonProperty("profileId")]
    public Guid ProfileId { get; set; }
    [Key(3)]
    [JsonProperty("amount")]
    public long Amount { get; set; }
    [Key(4)]
    [JsonProperty("timestamp")]
    public DateTime Timestamp { get; set; }
    [Key(5)]

    [JsonIgnore]
    public int BidderId { get; set; }

    public Bid() { }

    public override bool Equals(object obj)
    {
        return obj is Bid bids &&
               EqualityComparer<Auction>.Default.Equals(Auction, bids.Auction) &&
               Bidder == bids.Bidder &&
               ProfileId == bids.ProfileId &&
               Amount == bids.Amount &&
               Timestamp == bids.Timestamp;
    }

    public override int GetHashCode()
    {
        int hashCode = 291388595;
        hashCode = hashCode * -1521134295 + BidderId.GetHashCode();
        hashCode = hashCode * -1521134295 + Amount.GetHashCode();
        hashCode = hashCode * -1521134295 + Timestamp.GetHashCode();
        return hashCode;
    }
}