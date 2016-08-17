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
			/// <summary>
			/// For Async Writers, this is the number of frames a completed saver will stay alive for allowing it to be recycled
			/// </summary>
			public static int ZombieSurvivalAge
			{
				get
				{
					return 2;
				}
			}

			public int FramesUntilDead = ZombieSurvivalAge;

			public string Tag;

			public bool IsAvailable(DeviceContext Context, Texture2DDescription Description)
			{
				if (!this.Completed)
				{
					return false;
				}
				return this.FAssets.RenderDeviceContext == Context && this.FAssets.Description == Description;
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

		[Input("Write")]
		ISpread<bool> FInWrite;

		[Output("Tag")]
		ISpread<string> FOutTag;

		[Output("Status")]
		ISpread<string> FOutStatus;

		[Output("Success")]
		ISpread<bool> FOutSuccess;

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
						
						//attempt to recycle an existing saver
						foreach(var existingSaver in FSavers)
						{
							if(existingSaver.IsAvailable(AssignedContext.Device.ImmediateContext, FInTexture[i][AssignedContext].Description))
							{
								saver = existingSaver;
								break;
							}
						}

						//make a saver if none available
						if(saver == null)
						{
							saver = new SaverAsync();
						}

						saver.Tag = FInTag[i];
						saver.Save(AssignedContext.Adapter, FInTexture[i][AssignedContext], FInFilename[i], FInFormat[i]);

						FSavers.Add(saver);
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
						FOutTag.Add(saver.Tag);
						FOutStatus.Add(saver.Status);
						FOutSuccess.Add(!saver.Success);
					}
				}
			}

			//delete expired zombies
			{
				foreach(var saver in FSavers)
				{
					if(saver.Completed)
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