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
				Version = "Parallel",
				Help = "Write textures to disk in parallel",
				Tags = "",
				AutoEvaluate = true)]
	#endregion PluginInfo
	public class WriterParallel : IPluginEvaluate, IDX11ResourceDataRetriever, IDisposable
	{
		#region fields & pins
#pragma warning disable 0649
		[Input("Input")]
		Pin<DX11Resource<DX11Texture2D>> FInTexture;

		[Input("Format")]
		protected ISpread<Saver.ImageFileFormat> FInFormat;

		[Input("Filename", StringType = StringType.Filename)]
		ISpread<string> FInFilename;

		[Input("Write")]
		ISpread<bool> FInWrite;

		[Output("Status")]
		ISpread<string> FOutStatus;

		[Output("Valid")]
		ISpread<bool> FOutValid;

		[Import()]
		protected IPluginHost FHost;

		[Import]
		public ILogger FLogger;

		[Import]
		IHDEHost FHDEHost;

		List<Saver> FSavers = new List<Saver>();

#pragma warning restore 0649
		#endregion fields & pins

		// import host and hand it to base constructor
		[ImportingConstructor()]
		public WriterParallel(IPluginHost host)
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

			//setup the saver slots
			while(FSavers.Count < SpreadMax)
			{
				FSavers.Add(null);
			}
			while (FSavers.Count > SpreadMax)
			{
				if (FSavers[SpreadMax] != null)
				{
					FSavers[SpreadMax].Dispose();
				}
				FSavers.RemoveAt(SpreadMax);
			}

			FOutStatus.SliceCount = SpreadMax;
			FOutValid.SliceCount = SpreadMax;

			//perform the calls
			for (int i = 0; i < SpreadMax; i++)
			{
				if (FInWrite[i])
				{
					try
					{
						Saver saver;
						if(FSavers[i] != null)
						{
							saver = FSavers[i];
						} else
						{
							saver = new Saver();
							FSavers[i] = saver;
						}
						saver.Save(AssignedContext.Adapter, FInTexture[i][AssignedContext], FInFilename[i], FInFormat[i]);
					}
					catch (Exception e)
					{
						FOutStatus[i] = e.Message;
					}
				}
			}

			//wait for all calls to complete
			{
				while(true)
				{
					bool completed = true;
					foreach (var saver in FSavers)
					{
						if(saver != null)
						{
							if (!saver.Completed)
							{
								completed = false;
								break;
							}
						}
					}
					if (completed)
					{
						break;
					} else
					{
						Thread.Sleep(1);
					}
				}
			}

			//read back the status
			{
				for(int i=0; i<SpreadMax; i++)
				{
					var saver = FSavers[i];
					if(saver == null)
					{
						FOutValid[i] = false;
						FOutStatus[i] = "";
					} else
					{
						FOutValid[i] = saver.CompletedBang && saver.Success;
						FOutStatus[i] = saver.Status;
					}
				}
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
			foreach(var saver in this.FSavers)
			{
				if(saver != null)
				{
					saver.Dispose();
				}
			}
			FSavers.Clear();
		}
	}
}