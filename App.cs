using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace GoldDigger
{
	public class BlockToExplore
	{
		public BlockToExplore(int x, int y, int size)
		{
			this.X = x;
			this.Y = y;
			this.Size = size;
		}

		public int X { get; }
		public int Y { get; }
		public int Size { get; }

		public IEnumerable<BlockToExplore> Break()
		{
			// can be 14x14, 2x2, 1x1
			if (Size == 14)
			{
				for (int dx = 0; dx < 14; dx+=2)
					for (int dy = 0; dy < 14; dy+=2)
						yield return new BlockToExplore(X+dx,Y+dy,2);
			}
			else
			if (Size == 2)
			{
				yield return new BlockToExplore(X + 0, Y + 0, 1);
				yield return new BlockToExplore(X + 1, Y + 0, 1);
				yield return new BlockToExplore(X + 0, Y + 1, 1);
				yield return new BlockToExplore(X + 1, Y + 1, 1);
			}
		}
	}

	public class TreasureMap
	{
		public TreasureMap(int x, int y, int amount)
		{
			this.X = x;
			this.Y = y;
			this.Amount = amount;
		}

		public int X { get; }
		public int Y { get; }
		public int Amount { get; }
	}

	public class LicensePool
	{
		private readonly ConcurrentBag<int> coins;

		private readonly Api api;

		private readonly ConcurrentQueue<int> freeLicense = new ConcurrentQueue<int>();

		private readonly ConcurrentQueue<int> paidLicense = new ConcurrentQueue<int>();

		private readonly Task freeLicensePoll;
		
		private readonly Task paidLicensePoll;

		public LicensePool(Api api, ConcurrentBag<int> coins, CancellationToken token)
		{
			this.api = api;
			this.coins = coins;
			this.freeLicensePoll = PollFreeLicense(token);
			this.paidLicensePoll = PollPaidLicense(token);
		}

		private IEnumerable<int> TakeCoins(int count)
		{
			while (count-- > 0)
			{
				if (this.coins.TryTake(out var coin))
					yield return coin;
			}
		}

		private async Task PollPaidLicense(CancellationToken token)
		{
			int attempts = 1;
			while (!token.IsCancellationRequested)
			{
				if (paidLicense.Count > 10 || this.coins.Count < attempts)
				{
					await Task.Delay(50, token);
					continue;
				}

				var licenseCost = TakeCoins(attempts++).ToArray();

				int retryCount = 0;
				retry:
				try
				{
					var license = await this.api.IssueLicenseAsync(licenseCost, token);
					Console.WriteLine($"Retrieved paid license for {attempts-1} coins, allows {license.DigAllowed} digs.");
				}
				catch (Exception ex)
				{
					if (retryCount++ > 10)
					{
						Console.WriteLine("Something wrong with paid license." + ex);
						foreach (var coin in licenseCost)
							this.coins.Add(coin); // add coins back

						continue;
					}
					goto retry;
				}
			}
		}

		private async Task PollFreeLicense(CancellationToken token)
		{
			while (!token.IsCancellationRequested)
			{
				if (this.freeLicense.Count >= 9)
				{
					await Task.Delay(50, token);
					continue;
				}

				int getLicenseRetryCounter = 0;
				retry:
				try
				{
					var license = await this.api.IssueLicenseAsync(new int[0], token);

					foreach (var lic in Enumerable.Repeat(license.Id, license.DigAllowed))
						this.freeLicense.Enqueue(lic);
				}
				catch (Exception ex)
				{
					getLicenseRetryCounter++;
					if (getLicenseRetryCounter > 10)
					{
						Console.WriteLine("Something wrong, retry in free license:" + ex);
						continue;
					}
					goto retry;
				}
			}
		}

		public (int, int) LicenseCount()
		{
			return (this.freeLicense.Count, this.paidLicense.Count);
		}

		public void ReturnLicense(int license, bool free)
		{
			if (free)
				this.freeLicense.Enqueue(license);
			else
				this.paidLicense.Enqueue(license);
		}

		public async Task<int> GetFreeLicense()
		{
			int license;
			int waitCounter = 0;
			while (!this.freeLicense.TryDequeue(out license))
			{
				await Task.Delay(10);
				if (waitCounter++ > 20)
				{
					Console.WriteLine("Waited more than 200ms for free license");
					waitCounter = 0;
				}
			}

			return license;
		}

		public async Task<int> GetPaidLicense()
		{
			int license;
			int waitCounter = 0;
			while (!this.paidLicense.TryDequeue(out license))
			{
				await Task.Delay(10);
				if (waitCounter++ > 50)
				{
					Console.WriteLine("Waited more than 500ms for paid license");
					waitCounter = 0;
				}
			}

			return license;
		}
	}

	public class App
	{
		private readonly CancellationTokenSource ctsAppStop = new CancellationTokenSource();

		private readonly ConcurrentBag<int> coins = new ConcurrentBag<int>();
		private readonly List<BlockToExplore> initialBlocks = new List<BlockToExplore>();
		private volatile int currentBlock;

		private readonly ConcurrentQueue<string> recoveredTreasures = new ConcurrentQueue<string>();
		private readonly ConcurrentQueue<TreasureMap> treasuresToDig = new ConcurrentQueue<TreasureMap>();
		private readonly ConcurrentQueue<BlockToExplore>[] secondaryExploreQueue = new ConcurrentQueue<BlockToExplore>[10];

		private LicensePool licensePool;

		public static async Task Main()
		{
			Console.WriteLine($"{DateTime.Now}: Start");
			await new App().Run();
		}

		private async Task Run()
		{
			var end = DateTime.Now.AddMinutes(10);

			await Task.Yield();
			var address = Environment.GetEnvironmentVariable("ADDRESS") ?? "localhost";
			var uri = new UriBuilder("http", address, 8000).Uri;
			var api = new Api(uri.ToString(), new HttpClient());

			this.licensePool = new LicensePool(api, this.coins, this.ctsAppStop.Token);
			// break down into exploration blocks
			const int blockSize = 14;
			for (int x = 0; x < 3500; x+= blockSize)
			{
				for (int y = 0; y < 3500; y+= blockSize)
				{
					this.initialBlocks.Add(new BlockToExplore(x,y, blockSize));
				}
			}

			foreach (var i in Enumerable.Range(0,10))
				this.secondaryExploreQueue[i] = new ConcurrentQueue<BlockToExplore>();

			Shuffle(this.initialBlocks);

			while (true)
			{
				try
				{
					await api.HealthCheckAsync();
					break;
				}
				catch
				{
					await Task.Delay(10);
				}
			}

			Console.WriteLine($"{DateTime.Now}: Ready");

			var tasks = new List<Task>();

			tasks.Add(Explorer(api));
			tasks.Add(Explorer(api));
			tasks.Add(Explorer(api));

			tasks.Add(Explorer(api, false));
			tasks.Add(Explorer(api, false));
			tasks.Add(Explorer(api, false));
			tasks.Add(Explorer(api, false));
			tasks.Add(Digger(api));
			tasks.Add(Digger(api));
			tasks.Add(Digger(api));
			tasks.Add(Digger(api));
			tasks.Add(Digger(api));
			tasks.Add(Digger(api));
			tasks.Add(Digger(api));
			tasks.Add(Digger(api));
			tasks.Add(Seller(api));

			while (DateTime.Now < end)
			{
				await Task.Delay(TimeSpan.FromSeconds(10));
				var stats = api.Stats.Snapshot();
				var cb = this.currentBlock > this.initialBlocks.Count ? -1 : this.currentBlock;
				var lic = this.licensePool.LicenseCount();
				Console.WriteLine($"{DateTime.Now}: req={stats.RequestCount},fail={stats.FailureCount},ticks={stats.TimeSpent},pq={cb},sq={string.Join('/', secondaryExploreQueue.Select(q => q.Count))},dig={treasuresToDig.Count},tre={recoveredTreasures.Count},coin={this.coins.Count},lic={lic}");
			}

			tasks.Clear();
			this.ctsAppStop.Cancel();
			await Task.Delay(10);

			this.ctsAppStop.Dispose();
		}

		public async Task Seller(Api api)
		{
			while (!this.ctsAppStop.IsCancellationRequested)
			{
				if (!this.recoveredTreasures.TryDequeue(out var treasure))
				{
					await Task.Delay(100);
					continue;
				}
				
				int[] exchangedCoins;
				int retryCounter = 0;
				retry:
				try
				{
					exchangedCoins = (await api.CashAsync(treasure)).ToArray();
				}
				catch
				{
					//Console.WriteLine(ex);
					retryCounter++;
					if (retryCounter > 10)
					{
						this.recoveredTreasures.Enqueue(treasure); // give up
						continue;
					}
					goto retry;
				}

				foreach (var coin in exchangedCoins)
					this.coins.Add(coin);
			}
		}

		public async Task Digger(Api api)
		{
			while (!this.ctsAppStop.IsCancellationRequested)
			{
				if (!this.treasuresToDig.TryDequeue(out var map))
				{
					await Task.Delay(100);
					continue;
				}

				var remainingAmount = map.Amount;
				for (int depth = 1; depth <= 8; ++depth)
				{
					if (depth > 3 && remainingAmount == 1)
					{
						break; // probably no sense to dig deeper if we already recovered most of treasure with free license
					}
					var license = await (depth <= 3 ? this.licensePool.GetFreeLicense() : this.licensePool.GetPaidLicense());
					
					int digRetryCounter = 0;
					retryDig:
					try
					{
						var treasures = (await api.DigAsync(new Dig
							{Depth = depth, LicenseID = license, PosX = map.X, PosY = map.Y})).ToArray();

						foreach (var tr in treasures)
							this.recoveredTreasures.Enqueue(tr);

						remainingAmount -= treasures.Length;
						if (remainingAmount <= 0)
							break;
					}
					catch
					{
						//Console.WriteLine(ex);
						digRetryCounter++;
						if (digRetryCounter > 10)
						{
							this.licensePool.ReturnLicense(license, depth <= 3);
							this.treasuresToDig.Enqueue(map);
							break; // give up on this treasure
						}

						goto retryDig;
					}
				}

			}
		}

		public async Task Explorer(Api api, bool preferPrimaryQueue = true)
		{
			while (!this.ctsAppStop.IsCancellationRequested)
			{
				BlockToExplore block = null;
				if (preferPrimaryQueue)
				{
					var curBlockIdx = Interlocked.Increment(ref this.currentBlock);
					if (curBlockIdx < this.initialBlocks.Count)
						block = this.initialBlocks[curBlockIdx];
				}

				if (block == null)
				{
					foreach (var q in this.secondaryExploreQueue)
					{
						if (q.TryDequeue(out block))
							break;
					}

					if (block == null)
					{
						await Task.Delay(100);
						continue;
					}
				}

				int retryCount = 0;
				retry:
				try
				{
					using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
					var exploreResult = await api.ExploreAreaAsync(new Area { PosX = block.X, PosY = block.Y, SizeX = block.Size, SizeY = block.Size }, cts.Token);
					if (exploreResult.Amount > 0)
					{
						if (block.Size == 1)
							this.treasuresToDig.Enqueue(new TreasureMap(block.X, block.Y, exploreResult.Amount));
						else if (block.Size == 14)
						{
							if (exploreResult.Amount > 20) exploreResult.Amount = 20;
							var priority = 10 - exploreResult.Amount / 2;

							foreach (var smallerBlock in block.Break())
								this.secondaryExploreQueue[priority].Enqueue(smallerBlock);
						}
						else if (block.Size == 2)
						{
							var priority = exploreResult.Amount > 2 ? 0 : 1;
							foreach (var smallestBlock in block.Break())
								this.secondaryExploreQueue[priority].Enqueue(smallestBlock);
						}
					}
				}
				catch
				{
					if (retryCount++ > 10)
					{
						foreach (var smallerBlock in block.Break())
							this.secondaryExploreQueue[5].Enqueue(smallerBlock);

						continue; // give up block
					}

					goto retry;
				}
			}
		}

		public static void Shuffle<T>(IList<T> list)
		{
			var rnd = new Random();
			int n = list.Count;
			while (n > 1)
			{
				n--;
				int k = rnd.Next(n + 1);
				T value = list[k];
				list[k] = list[n];
				list[n] = value;
			}
		}
	}
}
