#region usings
using System;
using System.ComponentModel.Composition;
using System.Runtime.InteropServices;

using SlimDX;
//using SlimDX.Direct3D9;
using VVVV.Core.Logging;
using VVVV.PluginInterfaces.V1;
using VVVV.PluginInterfaces.V2;
using VVVV.PluginInterfaces.V2.EX9;
using VVVV.Utils.VColor;
using VVVV.Utils.VMath;
//using VVVV.Utils.SlimDX;

using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing.Imaging;
using System.Diagnostics;
using FeralTic.DX11.Resources;
using VVVV.DX11;
using System.Security.Permissions;
using FeralTic.DX11;
using SlimDX.Direct3D11;

#endregion usings

namespace VVVV.Nodes.DX11.ReadBack
{
	#region PluginInfo
	[PluginInfo(Name = "Writer",
				Category = "DX11.Texture2D",
				Version = "Async",
				Help = "Write textures to disk in background",
				Tags = "",
				AutoEvaluate = true)]
	#endregion PluginInfo
	public class WriterAsync : IPluginEvaluate, IDX11ResourceDataRetriever, IDisposable
	{
		class SaverAsync : Saver
		{
			public static int DefaultZombieSurvivalAge = 2;

			int FZombieSurvivalAge = DefaultZombieSurvivalAge;
			/// <summary>
			/// For Async Writers, this is the number of frames a completed saver will stay alive for allowing it to be recycled
			/// </summary>
			public int ZombieSurvivalAge {
				get
				{
					return FZombieSurvivalAge;
				}
				set
				{
					if(this.FramesUntilDead == this.ZombieSurvivalAge)
					{
						this.FramesUntilDead = value;
					}
					this.ZombieSurvivalAge = value;
				}
			}

			public int FramesUntilDead = DefaultZombieSurvivalAge;

			public string Tag;

			public bool IsAvailable(DeviceContext Context, Texture2DDescription Description)
			{
				if (!this.Completed)
				{
					return false;
				}
				return this.FAssets.RenderDeviceContext == Context && this.FAssets.Description == Description;
			}

			public override void Recycle()
			{
				FramesUntilDead = ZombieSurvivalAge;
				base.Recycle();
			}
		}
		
		#region fields & pins
#pragma warning disable 0649
		[Input("Input")]
		Pin<DX11Resource<DX11Texture2D>> FInTexture;

		[Input("Format")]
		protected ISpread<Saver.ImageFileFormat> FInFormat;

		[Input("Filename", StringType = StringType.Filename)]
		ISpread<string> FInFilename;

		[Input("Tag")]
		ISpread<string> FInTag;

		[Input("Max Saver Count", DefaultValue = 8, IsSingle = true)]
		ISpread<int> FInMaxSavers;

		[Input("Write")]
		ISpread<bool> FInWrite;

		[Input("Zombie Survival Age", DefaultValue = 2, IsSingle = true)]
		ISpread<int> FConfigZombieSurvivalAge;

		[Output("Filename")]
		ISpread<string> FOutFilename;

		[Output("Tag")]
		ISpread<string> FOutTag;

		[Output("Success")]
		ISpread<bool> FOutSuccess;

		[Output("Status")]
		ISpread<string> FOutStatus;

		[Output("Queue Size")]
		ISpread<int> FOutQueueSize;

		[Import()]
		protected IPluginHost FHost;

		[Import]
		public ILogger FLogger;

		[Import]
		IHDEHost FHDEHost;

		List<SaverAsync> FSavers = new List<SaverAsync>();

#pragma warning restore 0649
		#endregion fields & pins

		// import host and hand it to base constructor
		[ImportingConstructor()]
		public WriterAsync(IPluginHost host)
		{
		}

		public int QueueSize
		{
			get
			{
				int queueSize = 0;
				foreach(var saver in FSavers)
				{
					if(!saver.Completed)
					{
						queueSize++;
					}
				}
				return queueSize;
			}
		}

		void TrimSavers()
		{
			foreach (var saver in FSavers)
			{
				if (saver.Completed)
				{
					saver.FramesUntilDead--;
					if (saver.FramesUntilDead < 0)
					{
						saver.Dispose();
					}
				}
			}
			FSavers.RemoveAll(saver => saver.FramesUntilDead < 0);
		}

		public void Evaluate(int SpreadMax)
		{
			//Check our GPU connection is setup
			try
			{
				if (FInTexture.PluginIO.IsConnected)
				{
					if (this.RenderRequest != null) { this.RenderRequest(this, this.FHost); }
					if (this.AssignedContext == null) { return; }
				}
			}
			catch (Exception e)
			{
				FLogger.Log(e);
				FOutStatus.SliceCount = 1;
				FOutStatus[0] = e.Message;
				return;
			}

			FOutFilename.SliceCount = 0;
			FOutTag.SliceCount = 0;
			FOutStatus.SliceCount = 0;
			FOutSuccess.SliceCount = 0;

			//perform the calls
			for (int i = 0; i < SpreadMax; i++)
			{
				if (FInWrite[i])
				{
					try
					{
						SaverAsync saver = null;

						while (saver == null)
						{
							//attempt to recycle an existing saver
							foreach (var existingSaver in FSavers)
							{
								if (existingSaver.IsAvailable(AssignedContext.Device.ImmediateContext, FInTexture[i][AssignedContext].Description))
								{
									saver = existingSaver;
									break;
								}
							}

							//make a saver if none available (only if we haven't exceeded max count)
							if (saver == null && FSavers.Count < FInMaxSavers[0])
							{
								saver = new SaverAsync();
								saver.ZombieSurvivalAge = FConfigZombieSurvivalAge[0];
								FSavers.Add(saver);
							}

							if(saver == null)
							{
								//kill off expired savers (e.g. on other devices that we can't use)
								TrimSavers();
								Thread.Sleep(1);
							}
						}

						saver.Tag = FInTag[i];
						saver.Save(AssignedContext.Adapter, FInTexture[i][AssignedContext], FInFilename[i], FInFormat[i]);
					}
					catch (Exception e)
					{
						FOutTag.Add(FInTag[i]);
						FOutStatus.Add(e.Message);
						FOutSuccess.Add(false);
					}
				}
			}

			//read out the status of anything which completed this frame
			{
				foreach(var saver in FSavers)
				{
					if(saver.CompletedBang)
					{
						FOutFilename.Add(saver.Filename);
						FOutTag.Add(saver.Tag);
						FOutStatus.Add(saver.Status);
						FOutSuccess.Add(saver.Success);
					}
				}
			}

			//delete expired zombies
			//TrimSavers();

			FOutQueueSize[0] = this.QueueSize;
		}

		public FeralTic.DX11.DX11RenderContext AssignedContext
		{
			get;
			set;
		}

		public event DX11RenderRequestDelegate RenderRequest;

		public void Dispose()
		{
			foreach (var saver in this.FSavers)
			{
				if (saver != null)
				{
					saver.Dispose();
				}
			}
			FSavers.Clear();
		}
	}
}