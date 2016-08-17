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

namespace VVVV.Nodes.Recorder
{
	#region PluginInfo
	[PluginInfo(Name = "Writer",
				Category = "DX11.Texture2D",
				Version = "Parallel",
				Help = "Write textures to disk in parallel",
				Tags = "",
				AutoEvaluate = true)]
	#endregion PluginInfo
	public class RecordNodeDX11 : IPluginEvaluate, IDX11ResourceDataRetriever, IDisposable
	{
		public enum ImageFileFormat { Bmp = 1, Jpeg = 2, Png = 3, Tiff = 4, Gif = 5, Hdp = 6, Dds = 128, Tga = 129 }

		class Saver : IDisposable
		{
			public bool Completed { get; private set; } = false;
			public bool Failed { get; private set; } = false;

			bool FCompletedBang = false;
			public bool CompletedBang
			{
				get
				{
					if(FCompletedBang)
					{
						FCompletedBang = false;
						return true;
					} else
					{
						return false;
					}
				}
				set
				{
					FCompletedBang = value;
				}
			}

			string FStatus = "";
			public string Status
			{
				get
				{
					string copy;
					lock (FStatus)
					{
						copy = FStatus;
					}
					return copy;
				}
				set
				{
					lock (FStatus)
					{
						FStatus = value;
					}
				}
			}

			class Assets : IDisposable
			{
				public DeviceContext RenderDeviceContext = null;
				public Device RenderDevice = null;

				public Texture2DDescription Description;
				public Texture2DDescription SharedDescription;

				public Texture2D SharedTextureOnRenderDevice;

				public Device SaveDevice = null;
				public DeviceContext SaveDeviceContext = null;

				public Texture2D SharedTextureOnSaveDevice;
				public Texture2D StagingTextureOnSaveDevice;

				public void Dispose()
				{
					if(StagingTextureOnSaveDevice != null)
					{
						StagingTextureOnSaveDevice.Dispose();
						StagingTextureOnSaveDevice = null;
					}

					if(SharedTextureOnSaveDevice != null)
					{
						SharedTextureOnSaveDevice.Dispose();
						SharedTextureOnSaveDevice = null;
					}

					if(SaveDevice != null)
					{
						SaveDeviceContext.Dispose();
						SaveDevice.Dispose();

						SaveDevice = null;
						SaveDeviceContext = null;
					}

					if(SharedTextureOnRenderDevice != null)
					{
						SharedTextureOnRenderDevice.Dispose();
						SharedTextureOnRenderDevice = null;
					}

					Description = new Texture2DDescription();
					SharedDescription = new Texture2DDescription();
				}
			}
			Assets FAssets = new Assets();

			Thread FThread;

			public Saver()
			{
			}
			
			public void Save(SlimDX.DXGI.Adapter adapter, DX11Texture2D texture, string filename, ImageFileFormat format)
			{
				try
				{
					//log the render device context
					if (this.FAssets.RenderDeviceContext != texture.Resource.Device.ImmediateContext)
					{
						this.FAssets.Dispose();

						this.FAssets.RenderDeviceContext = texture.Resource.Device.ImmediateContext;
						this.FAssets.RenderDevice = texture.Resource.Device;
					}

					//allocate textures
					if (FAssets.Description != texture.Description)
					{
						this.FAssets.Dispose();

						FAssets.Description = texture.Description;
						FAssets.SharedDescription = texture.Description;
						{
							FAssets.SharedDescription.Usage = ResourceUsage.Default;
							FAssets.SharedDescription.OptionFlags |= ResourceOptionFlags.Shared;
						}
						FAssets.SharedTextureOnRenderDevice = new Texture2D(this.FAssets.RenderDevice, FAssets.SharedDescription);
					}

					//copy input texture to shared texture
					{
						this.FAssets.RenderDeviceContext.CopyResource(texture.Resource, FAssets.SharedTextureOnRenderDevice);
					}

					//create a device/context for saving
					if (FAssets.SaveDevice == null)
					{

#if DEBUG
						FAssets.SaveDevice = new SlimDX.Direct3D11.Device(adapter, DeviceCreationFlags.Debug);
#else
						FAssets.SaveDevice = new SlimDX.Direct3D11.Device(adapter);
#endif
						FAssets.SaveDeviceContext = FAssets.SaveDevice.ImmediateContext;
					}

					//flush the immediate context to ensure that our shared texture is available
					{
						var queryDescription = new QueryDescription(QueryType.Event, QueryFlags.None);
						var query = new Query(FAssets.RenderDevice, queryDescription);
						FAssets.RenderDeviceContext.Flush();
						FAssets.RenderDeviceContext.End(query);
						var result = FAssets.RenderDeviceContext.GetData<bool>(query);
						while (!result)
						{
							result = FAssets.RenderDeviceContext.GetData<bool>(query);
							Thread.Sleep(1);
						}
					}

					//create the textures on saving device
					if(FAssets.SharedTextureOnSaveDevice == null)
					{
						SlimDX.DXGI.Resource r = new SlimDX.DXGI.Resource(FAssets.SharedTextureOnRenderDevice);
						FAssets.SharedTextureOnSaveDevice = FAssets.SaveDevice.OpenSharedResource<Texture2D>(r.SharedHandle);

						var stagingTextureDescription = texture.Description;
						{
							stagingTextureDescription.Usage = ResourceUsage.Default;
						}

						FAssets.StagingTextureOnSaveDevice = new Texture2D(FAssets.SaveDevice, stagingTextureDescription);

					}

					//copy the shared texture into a staging texture (actually for our purpose, we will not use an actual staging texture since we use SaveTextureToFile which handles staging for us)
					FAssets.SaveDeviceContext.CopyResource(FAssets.SharedTextureOnSaveDevice, FAssets.StagingTextureOnSaveDevice);

					this.FThread = new Thread(() =>
					{
						try
						{
							//gain rights to write to file
							(new FileIOPermission(FileIOPermissionAccess.Write, filename)).Demand();

							//perform read back and save in thread
							try
							{
								var directory = Path.GetDirectoryName(filename);
								if(!Directory.Exists(directory))
								{
									Directory.CreateDirectory(directory);
								}
								FeralTic.DX11.Resources.TextureLoader.NativeMethods.SaveTextureToFile(FAssets.SaveDevice.ComPointer
									, FAssets.SaveDeviceContext.ComPointer
									, FAssets.StagingTextureOnSaveDevice.ComPointer
									, filename
									, (int)format);
							}
							catch (Exception e)
							{
								throw (new Exception("SaveTextureToFile failed : " + e.Message));
							}

							this.CompletedBang = true;
							this.Completed = true;
							this.Status = "OK";
						}
						catch (Exception e)
						{
							this.Completed = true;
							this.Status = e.Message;
							this.Failed = true;
						}
					});

					this.FThread.Name = "DX11 read";
					this.FThread.Start();
				}

				catch (Exception e)
				{
					this.Completed = true;
					this.Failed = true;
					this.Status = e.Message;
				}
			}

			public void Dispose()
			{
				if(!this.Completed)
				{
					if(this.FThread != null)
					{
						if(!this.FThread.Join(1000))
						{
							this.FThread.Abort();
						}
						this.FThread = null;
					}
					this.Completed = true;

					FAssets.Dispose();
				}
			}
		};


		#region fields & pins
#pragma warning disable 0649
		[Input("Input")]
		Pin<DX11Resource<DX11Texture2D>> FInTexture;

		[Input("Format")]
		protected ISpread<ImageFileFormat> FInFormat;

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
		public RecordNodeDX11(IPluginHost host)
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
						FOutValid[i] = saver.CompletedBang;
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