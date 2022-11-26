using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using dev;
using Coflnet.Sky.Core;
using System.Collections.Generic;

namespace Coflnet.Sky.Indexer
{
    public class NameUpdater
    {
        public static DateTime LastUpdate { get; internal set; }
        private static ConcurrentQueue<IdAndName> newPlayers = new ConcurrentQueue<IdAndName>();

        private static int updateCount = 0;
        static Prometheus.Counter nameUpdateCounter = Prometheus.Metrics.CreateCounter("sky_indexer_name_update", "Tracks the count of updated mc names");

        private class IdAndName
        {
            public string Uuid;
            public string Name;
        }

        public static async Task<int> UpdateFlaggedNames()
        {
            var updated = 0;
            var targetAmount = 60;
            var players = PlayersToUpdate(targetAmount);
            foreach (var player in players)
            {
                var name = await Sky.Core.Program.GetPlayerNameFromUuid(player.UuId);
                if (name == null)
                {
                    // indicates something went wrong
                    await Task.Delay(200);
                }
                using var context = new HypixelContext();
                var playerToUpdate = context.Players.Where(p => p.UuId == player.UuId).First();
                if (name != null)
                    playerToUpdate.Name = name;
                playerToUpdate.ChangedFlag = false;
                player.UpdatedAt = DateTime.Now;
                context.Players.Update(playerToUpdate);
                nameUpdateCounter.Inc();
                try
                {
                    updated += await context.SaveChangesAsync();
                }
                catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
                {
                    dev.Logger.Instance.Error("could not update player, already modified " + Newtonsoft.Json.JsonConvert.SerializeObject(player));
                    await Task.Delay(2000);
                }
            }

            LastUpdate = DateTime.Now;
            updateCount++;
            return updated;
        }

        private static List<Player> PlayersToUpdate(int targetAmount)
        {
            List<Player> players;
            using (var context = new HypixelContext())
            {
                players = context.Players.Where(p => p.ChangedFlag && p.Id > 0)
                    .OrderBy(p => p.UpdatedAt)
                    .Take(targetAmount).ToList();
            }

            return players;
        }

        public static void Run()
        {
            Task.Run(async () =>
            {
                // set the start time to not return bad status
                LastUpdate = DateTime.Now;
                await Task.Delay(TimeSpan.FromSeconds(3));
                await RunForever();
            }).ConfigureAwait(false);
        }

        internal static void UpdateUUid(string id, string name = null)
        {
            newPlayers.Enqueue(new IdAndName() { Name = name, Uuid = id });
        }

        static async Task RunForever()
        {
            while (true)
            {
                try
                {
                    await FlagChanged();
                    var count = await UpdateFlaggedNames();
                    if (count < 5)
                        await FlagOldest();
                    Console.WriteLine($" - Updated flagged player names ({count}) - ");
                }
                catch (Exception e)
                {
                    Logger.Instance.Error($"NameUpdater encountered an error \n {e.Message} {e.StackTrace} \n{e.InnerException?.Message} {e.InnerException?.StackTrace}");
                }
                await Task.Delay(5000);
            }
        }

        private static async Task FlagChanged()
        {
            if (newPlayers.Count() == 0)
                return;
            using (var context = new HypixelContext())
            {
                while (newPlayers.TryDequeue(out IdAndName result))
                {
                    var player = context.Players.Where(p => p.UuId == result.Uuid).FirstOrDefault();
                    if (player != null)
                    {
                        player.ChangedFlag = true;
                        player.Name = result.Name;
                        context.Players.Update(player);
                        continue;
                    }
                    Sky.Core.Program.AddPlayer(context, result.Uuid, ref Indexer.highestPlayerId, result.Name);
                }
                await context.SaveChangesAsync();
            }
        }

        static async Task FlagOldest()
        {
            // this is a workaround, because the "updatedat" field is only updated when there is a change
            using (var context = new HypixelContext())
            {
                var players = context.Players.Where(p => p.Id > 0)
                    .OrderBy(p => p.UpdatedAt).Take(50);
                players = players.Concat(context.Players.Where(p => !p.ChangedFlag && p.Name == null));
                foreach (var p in players)
                {
                    p.ChangedFlag = true;
                    context.Players.Update(p);
                }
                await context.SaveChangesAsync();
            }
            await Task.Delay(10000);
        }
    }
}