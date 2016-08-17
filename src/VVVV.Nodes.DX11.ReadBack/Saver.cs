using FeralTic.DX11.Resources;
using SlimDX.Direct3D11;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Permissions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VVVV.Nodes.DX11.ReadBack
{
	public class Saver : IDisposable
	{
		public enum ImageFileFormat { Bmp = 1, Jpeg = 2, Png = 3, Tiff = 4, Gif = 5, Hdp = 6, Dds = 128, Tga = 129 }

		public bool Completed { get; private set; } = false;
		public bool Success { get; private set; } = false;

		bool FCompletedBang = false;
		public bool CompletedBang
		{
			get
			{
				if (FCompletedBang)
				{
					FCompletedBang = false;
					return true;
				}
				else
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

		protected class Assets : IDisposable
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
				if (StagingTextureOnSaveDevice != null)
				{
					StagingTextureOnSaveDevice.Dispose();
					StagingTextureOnSaveDevice = null;
				}

				if (SharedTextureOnSaveDevice != null)
				{
					SharedTextureOnSaveDevice.Dispose();
					SharedTextureOnSaveDevice = null;
				}

				if (SaveDevice != null)
				{
					SaveDeviceContext.Dispose();
					SaveDevice.Dispose();

					SaveDevice = null;
					SaveDeviceContext = null;
				}

				if (SharedTextureOnRenderDevice != null)
				{
					SharedTextureOnRenderDevice.Dispose();
					SharedTextureOnRenderDevice = null;
				}

				Description = new Texture2DDescription();
				SharedDescription = new Texture2DDescription();
			}
		}
		protected Assets FAssets = new Assets();

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
				if (FAssets.SharedTextureOnSaveDevice == null)
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
							if (!Directory.Exists(directory))
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
						this.Success = true;
					}
					catch (Exception e)
					{
						this.Completed = true;
						this.Status = e.Message;
						this.Success = false;
					}
				});

				this.FThread.Name = "DX11 read";
				this.FThread.Start();
			}

			catch (Exception e)
			{
				this.Completed = true;
				this.Success = false;
				this.Status = e.Message;
			}
		}

		public void Dispose()
		{
			if (!this.Completed)
			{
				if (this.FThread != null)
				{
					if (!this.FThread.Join(1000))
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
}
