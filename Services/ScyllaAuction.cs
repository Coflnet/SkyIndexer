using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cassandra.Data.Linq;
using Cassandra.Mapping;
using Coflnet.Sky.Core;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Coflnet.Sky.Indexer;
public class CassandraAuction : ICassandraItem
{
    [Cassandra.Mapping.Attributes.SecondaryIndex()]
    public Guid Uuid { get; set; }
    [Cassandra.Mapping.Attributes.PartitionKey]
    public string Tag { get; set; }
    public string ItemName { get; set; }
    public string ItemLore { get; set; }
    public string Extra { get; set; }
    public string Category { get; set; }
    public string Tier { get; set; }
    public long StartingBid { get; set; }
    public long HighestBidAmount { get; set; }
    public bool Bin { get; set; }
    [Cassandra.Mapping.Attributes.ClusteringKey]
    public DateTime End { get; set; }
    public DateTime Start { get; set; }
    public DateTime ItemCreatedAt { get; set; }
    public Guid Auctioneer { get; set; }
    public Guid ProfileId { get; set; }
    public string ProfileName { get; set; }
    public List<string> Coop { get; set; }
    public string CoopName { get; set; }
    public Guid HighestBidder { get; set; }
    public string HighestBidderName { get; set; }
    public string ExtraAttributesJson { get; set; }
    public Dictionary<string, string> NbtLookup { get; set; }
    public string ItemBytes { get; set; }
    public long ItemUid { get; set; }
    public long? Id
    {
        get
        {
            return ItemUid;
        }
        set
        {
            ItemUid = value.Value;
        }
    }
    public Guid ItemId { get; set; }
    public Dictionary<string, int> Enchantments { get; set; }
    public int? Color { get; set; }
}

public class CassandraBid
{
    public Guid AuctionUuid { get; set; }
    public Guid BidderUuid { get; set; }
    public string BidderName { get; set; }
    public long Amount { get; set; }
    public DateTime Timestamp { get; set; }
    public Guid ProfileId { get; set; }

}

public class ScyllaService
{
    public Cassandra.ISession Session { get; set; }
    private Table<CassandraAuction> AuctionsTable { get; set; }
    private Table<CassandraBid> BidsTable { get; set; }
    private ILogger<ScyllaService> Logger { get; set; }
    public ScyllaService(Cassandra.ISession session, ILogger<ScyllaService> logger)
    {
        Session = session;
        Logger = logger;
    }

    public async Task Create()
    {
        Logger.LogInformation("Creating tables");
        var auctionsTable = GetAuctionsTable();
        var bidsTable = GetBidsTable();
        await auctionsTable.CreateIfNotExistsAsync();
        await bidsTable.CreateIfNotExistsAsync();

        AuctionsTable = auctionsTable;
        BidsTable = bidsTable;
        Logger.LogInformation("Created tables");
        await Task.Delay(1000);
    }

    public async Task InsertAuction(SaveAuction auction)
    {
        var colorString = auction.FlatenedNBT.GetValueOrDefault("color");
        int? color = null;
        if (colorString != null)
            color = (int)NBT.GetColor(colorString);
        await AuctionsTable.Insert(new CassandraAuction()
        {
            Auctioneer = Guid.Parse(auction.AuctioneerId),
            Bin = auction.Bin,
            Category = auction.Category.ToString(),
            Coop = auction.Coop,
            Color = color,
            Enchantments = auction.Enchantments.ToDictionary(e => e.Type.ToString(), e => (int)e.Level),
            End = auction.End,
            HighestBidAmount = auction.HighestBidAmount,
            HighestBidder = auction.Bids.Count == 0 ? Guid.Empty : Guid.Parse(auction.Bids.OrderByDescending(b => b.Amount).First().Bidder),
            ItemName = auction.ItemName,
            Tag = auction.Tag,
            Tier = auction.Tier.ToString(),
            StartingBid = auction.StartingBid,
            ExtraAttributesJson = JsonConvert.SerializeObject( auction.NbtData.Data),
            ItemUid = long.Parse(auction.FlatenedNBT.GetValueOrDefault("uid"), System.Globalization.NumberStyles.HexNumber),
            Uuid = Guid.Parse(auction.Uuid),
            Start = auction.Start,
            ItemCreatedAt = auction.ItemCreatedAt,
            ProfileId = Guid.Parse(auction.ProfileId),
            NbtLookup = auction.FlatenedNBT,
            
        }).ExecuteAsync();
    }

    private Table<CassandraAuction> GetAuctionsTable()
    {
        var mapping = new MappingConfiguration()
            .Define(new Map<CassandraAuction>()
            .PartitionKey(t => t.Tag).ClusteringKey(new Tuple<string, SortOrder>("end", SortOrder.Ascending), new("itemuid", SortOrder.Descending))
            // secondary index
            .Column(t => t.Uuid, cm => cm.WithSecondaryIndex())
            .Column(t => t.Auctioneer, cm => cm.WithSecondaryIndex())
            .Column(t => t.HighestBidder, cm => cm.WithSecondaryIndex())
            .Column(t => t.HighestBidder, cm => cm.WithSecondaryIndex())
            .Column(t => t.ItemId, cm => cm.WithSecondaryIndex())
        );
        return new Table<CassandraAuction>(Session, mapping, "auctions");
    }

    private Table<CassandraBid> GetBidsTable()
    {
        var mapping = new MappingConfiguration()
            .Define(new Map<CassandraBid>()
            .PartitionKey(t => t.BidderUuid).ClusteringKey(new Tuple<string, SortOrder>("timestamp", SortOrder.Descending))
            // secondary index
            .Column(t => t.AuctionUuid, cm => cm.WithSecondaryIndex())
        );
        return new Table<CassandraBid>(Session, mapping, "bids");
    }
}