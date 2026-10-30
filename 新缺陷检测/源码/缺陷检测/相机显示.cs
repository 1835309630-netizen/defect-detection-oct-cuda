using System;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using System.IO;
using GxIAPINET;
using HANDLE = System.IntPtr;
using HDC = System.IntPtr;
using HBITMAP = System.IntPtr;
using LPVOID = System.IntPtr;
using HINSTANCE = System.IntPtr;
using LPSTR = System.String;
using LPCSTR = System.String;
using LPWSTR = System.String;
using LPCWSTR = System.String;
using LPTSTR = System.String;
using LPCTSTR = System.String;
using DWORD = System.UInt32;
using UINT = System.UInt32;
using ULONG = System.UInt32;
using COLORREF = System.UInt32;
using INT = System.Int32;
using LONG = System.Int32;
using BOOL = System.Int32;
using BYTE = System.Byte;
using SHORT = System.Int16;
using WORD = System.UInt16;
using CHAR = System.Char;
using FLOAT = System.Single;
using DOUBLE = System.Double;

// 大恒官方示例来源：GxBitmap.cs
namespace GxIAPINET.Sample.Common
{
  public  class GxBitmap
    {
      IGXDevice                m_objIGXDevice          = null;                ///<设备对像
      PictureBox               m_pic_ShowImage         = null;                ///<图片显示控件
      bool                     m_bIsColor              = false;               ///<是否支持彩色相机
      byte[]                   m_byMonoBuffer          = null;                ///<黑白相机buffer
      byte[]                   m_byColorBuffer         = null;                ///<彩色相机buffer
      byte[]                   m_byRawBuffer           = null;                ///<用于存储Raw图的Buffer
      int                      m_nPayloadSize          = 0;                   ///<图像数据大小
      int                      m_nWidth                = 0;                   ///<图像宽度
      int                      m_nHeight               = 0;                   ///<图像高度
      Bitmap                   m_bitmapForSave         = null;                ///<bitmap对象,仅供存储图像使用
      const uint               PIXEL_FORMATE_BIT       = 0x00FF0000;          ///<用于与当前的数据格式进行与运算得到当前的数据位数
      const uint               GX_PIXEL_8BIT           = 0x00080000;          ///<8位数据图像格式
      const int                COLORONCOLOR            = 3;
      const uint               DIB_RGB_COLORS          = 0;
      const uint               SRCCOPY                 = 0x00CC0020;
      CWin32Bitmaps.BITMAPINFO m_objBitmapInfo         = new CWin32Bitmaps.BITMAPINFO();
      IntPtr                   m_pBitmapInfo           = IntPtr.Zero;
      Graphics                 m_objGC                 = null;
      IntPtr                   m_pHDC                  = IntPtr.Zero;
      IGXImageFormatConvert    m_objImageFormatConvert = null;                ///<IGXImageFormatConvert对象，仅供图像格式转换使用
      IGXImageProcess          m_objImageProcess       = null;                ///<IGXImageProcess对象，仅供图像质量提升使用
      IntPtr                   m_pOutBuffer            = IntPtr.Zero;         ///<图像质量提升或图像格式转换输出Buffer
                                                       
      /// <summary>
      /// 构造函数用于初始化设备对象与PictureBox控件对象
      /// </summary>
      /// <param name="objIGXDevice">设备对象</param>
      /// <param name="objPictureBox">图像显示控件</param>
      public GxBitmap(IGXDevice objIGXDevice, PictureBox objPictureBox, IGXStream objIGXStream, IGXFactory objIGXFactory)
      {
          // 初始化图像格式转换对象
          m_objImageFormatConvert = objIGXFactory.CreateImageFormatConvert();

          // 初始化图像处理对象
          m_objImageProcess = objIGXFactory.CreateImageProcess();

          m_objIGXDevice = objIGXDevice;
          m_pic_ShowImage = objPictureBox;
          if (null != objIGXDevice)
          {
              //获得图像原始数据大小、宽度、高度等
              m_nPayloadSize = (int)objIGXStream.GetPayloadSize();
              m_nWidth       = (int)objIGXDevice.GetRemoteFeatureControl().GetIntFeature("Width").GetValue();
              m_nHeight      = (int)objIGXDevice.GetRemoteFeatureControl().GetIntFeature("Height").GetValue();

              //获取是否为彩色相机
                IsSupportColor(ref m_bIsColor);
          }

          //申请用于缓存图像数据的buffer
          m_byRawBuffer = new byte[m_nPayloadSize];
          m_byMonoBuffer = new byte[__GetStride(m_nWidth, m_bIsColor) * m_nHeight];
          m_byColorBuffer = new byte[__GetStride(m_nWidth, m_bIsColor) * m_nHeight];
          m_pOutBuffer = Marshal.AllocCoTaskMem(__GetStride(m_nWidth, m_bIsColor) * m_nHeight);
          __CreateBitmap(out m_bitmapForSave, m_nWidth, m_nHeight, m_bIsColor);

          m_objGC = m_pic_ShowImage.CreateGraphics();
          m_pHDC = m_objGC.GetHdc();
          if (m_bIsColor)
          {
              m_objBitmapInfo.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(CWin32Bitmaps.BITMAPINFOHEADER));
              m_objBitmapInfo.bmiHeader.biWidth = m_nWidth;
              m_objBitmapInfo.bmiHeader.biHeight = m_nHeight;
              m_objBitmapInfo.bmiHeader.biPlanes = 1;
              m_objBitmapInfo.bmiHeader.biBitCount = 24;
              m_objBitmapInfo.bmiHeader.biCompression = 0;
              m_objBitmapInfo.bmiHeader.biSizeImage = 0;
              m_objBitmapInfo.bmiHeader.biXPelsPerMeter = 0;
              m_objBitmapInfo.bmiHeader.biYPelsPerMeter = 0;
              m_objBitmapInfo.bmiHeader.biClrUsed = 0;
              m_objBitmapInfo.bmiHeader.biClrImportant = 0;
          }
          else
          {
              m_objBitmapInfo.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(CWin32Bitmaps.BITMAPINFOHEADER));
              m_objBitmapInfo.bmiHeader.biWidth = m_nWidth;
              m_objBitmapInfo.bmiHeader.biHeight = m_nHeight;
              m_objBitmapInfo.bmiHeader.biPlanes = 1;
              m_objBitmapInfo.bmiHeader.biBitCount = 8;
              m_objBitmapInfo.bmiHeader.biCompression = 0;
              m_objBitmapInfo.bmiHeader.biSizeImage = 0;
              m_objBitmapInfo.bmiHeader.biXPelsPerMeter = 0;
              m_objBitmapInfo.bmiHeader.biYPelsPerMeter = 0;
              m_objBitmapInfo.bmiHeader.biClrUsed = 0;
              m_objBitmapInfo.bmiHeader.biClrImportant = 0;

              m_objBitmapInfo.bmiColors = new CWin32Bitmaps.RGBQUAD[256];
              // 黑白图像需要初始化调色板
              for (int i = 0; i < 256; i++)
              {
                  m_objBitmapInfo.bmiColors[i].rgbBlue     = (byte)i;
                  m_objBitmapInfo.bmiColors[i].rgbGreen    = (byte)i;
                  m_objBitmapInfo.bmiColors[i].rgbRed      = (byte)i;
                  m_objBitmapInfo.bmiColors[i].rgbReserved = 0;
              }
          }
          m_pBitmapInfo = Marshal.AllocHGlobal(2048);
          Marshal.StructureToPtr(m_objBitmapInfo, m_pBitmapInfo, false);
      }

      /// <summary>
      /// 析构函数
      /// </summary>
      ~GxBitmap()
      {
          // 释放输出Buffer内存
          ReleaseBuffer();
      }

      /// <summary>
      /// 释放buffer
      /// </summary>
      public void ReleaseBuffer()
      {
          // 释放输出Buffer内存
          if (m_pOutBuffer != IntPtr.Zero)
          {
              Marshal.FreeCoTaskMem(m_pOutBuffer);
              m_pOutBuffer = IntPtr.Zero;
          }

          if (m_pBitmapInfo != IntPtr.Zero)
          {
              Marshal.FreeHGlobal(m_pBitmapInfo);
              m_pBitmapInfo = IntPtr.Zero;
          }
      }

      /// <summary>
      /// 用于图像处理后并显示图像
      /// </summary>
      /// <param name="objCfg">图像处理参数配置对象</param>
      /// <param name="objIBaseData">图像数据对象</param>
      public void ShowImageProcess(IImageProcessConfig objCfg, IBaseData objIBaseData)
      {
          //检查图像是否改变并更新Buffer
          __UpdateBufferSize(objIBaseData);

          if (null != objIBaseData)
          {
              if (GX_FRAME_STATUS_LIST.GX_FRAME_STATUS_SUCCESS == objIBaseData.GetStatus())
              {
                  objCfg.EnableConvertFlip(true);
                  if (m_bIsColor)
                  {
                      m_objImageProcess.ImageImprovment(objIBaseData, m_pOutBuffer, objCfg);
                      Marshal.Copy(m_pOutBuffer, m_byColorBuffer, 0, __GetStride(m_nWidth, m_bIsColor) * m_nHeight);

                      __ShowImage(m_byColorBuffer);
                  }
                  else
                  {
                      m_objImageProcess.ImageImprovment(objIBaseData, m_pOutBuffer, objCfg);
                      Marshal.Copy(m_pOutBuffer, m_byMonoBuffer, 0, __GetStride(m_nWidth, m_bIsColor) * m_nHeight);


                      __ShowImage(m_byMonoBuffer);
                  }
              }
          }       
      }
      

      /// <summary>
      /// 用于显示图像
      /// </summary>
      /// <param name="objIBaseData">图像数据对象</param>
      public void Show(IBaseData objIBaseData)
      {
          GX_VALID_BIT_LIST emValidBits = GX_VALID_BIT_LIST.GX_BIT_0_7;
          
          //检查图像是否改变并更新Buffer
          __UpdateBufferSize(objIBaseData);
       

          if (null != objIBaseData)
          {
              emValidBits = GetBestValudBit(objIBaseData.GetPixelFormat());
              if (GX_FRAME_STATUS_LIST.GX_FRAME_STATUS_SUCCESS == objIBaseData.GetStatus())
              {
                  if (m_bIsColor)
                  {
                      if (objIBaseData.GetPixelFormat() == GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BGR8)
                      {

                          byte[] byColorBufferTmp = new byte[__GetStride(m_nWidth, m_bIsColor) * m_nHeight];
                          Marshal.Copy(objIBaseData.GetBuffer(), byColorBufferTmp, 0, __GetStride(m_nWidth, m_bIsColor) * m_nHeight);

                          // BGR8格式需要翻转数据后显示
                          for (int i = 0; i < m_nHeight; i++)
                          {
                              Buffer.BlockCopy(byColorBufferTmp, (m_nHeight - i - 1) * m_nWidth * 3, m_byColorBuffer, i * m_nWidth * 3, m_nWidth * 3);
                          }

                      }
                      else
                      {
                          // 设置目标像素格式
                          m_objImageFormatConvert.SetDstFormat(GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BGR8);

                          // 获取目标像素格式Buffer大小
                          UInt64 i64DstBufferize = m_objImageFormatConvert.GetBufferSizeForConversion(objIBaseData);

                          // 设置目标有效位数
                          m_objImageFormatConvert.SetValidBits(emValidBits);

                          // 进行图像格式转换
                          m_objImageFormatConvert.Convert(objIBaseData, m_pOutBuffer, i64DstBufferize, true);
                          Marshal.Copy(m_pOutBuffer, m_byColorBuffer, 0, __GetStride(m_nWidth, m_bIsColor) * m_nHeight);
                      }

                      __ShowImage(m_byColorBuffer);
                  }
                  else
                  {
                      if (__IsPixelFormat8(objIBaseData.GetPixelFormat()))
                      {

                          byte[] byMonoBufferTmp = new byte[__GetStride(m_nWidth, m_bIsColor) * m_nHeight];
                          Marshal.Copy(objIBaseData.GetBuffer(), byMonoBufferTmp, 0, __GetStride(m_nWidth, m_bIsColor) * m_nHeight);

                          // 黑白相机需要翻转数据后显示
                          for (int i = 0; i < m_nHeight; i++)
                          {
                              Buffer.BlockCopy(byMonoBufferTmp, (m_nHeight - i - 1) * m_nWidth, m_byMonoBuffer, i * m_nWidth, m_nWidth);
                          }
                      }
                      else
                      {
                          // 设置目标像素格式
                          m_objImageFormatConvert.SetDstFormat(GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_MONO8);

                          // 获取目标像素格式Buffer大小
                          UInt64 i64DstBufferize = m_objImageFormatConvert.GetBufferSizeForConversion(objIBaseData);

                          // 设置目标有效位数
                          m_objImageFormatConvert.SetValidBits(emValidBits);

                          // 进行图像格式转换
                          m_objImageFormatConvert.Convert(objIBaseData, m_pOutBuffer, i64DstBufferize, true);

                          Marshal.Copy(m_pOutBuffer, m_byMonoBuffer, 0, __GetStride(m_nWidth, m_bIsColor) * m_nHeight);
                      }

                      __ShowImage(m_byMonoBuffer);
                  }
              }
          }
      }

      /// <summary>
      /// 存储图像
      /// </summary>
      /// <param name="objIBaseData">图像数据对象</param>
      /// <param name="strFilePath">显示图像文件名</param>
      public void SaveBmp(IBaseData objIBaseData, string strFilePath)
      {
          GX_VALID_BIT_LIST emValidBits = GX_VALID_BIT_LIST.GX_BIT_0_7;

          //检查图像是否改变并更新Buffer
          __UpdateBufferSize(objIBaseData);

          if (null != objIBaseData)
          {
              emValidBits = GetBestValudBit(objIBaseData.GetPixelFormat());
              if (m_bIsColor)
              {
                    if (objIBaseData.GetPixelFormat() == GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BGR8)
                    {
                        Marshal.Copy(objIBaseData.GetBuffer(), m_byColorBuffer, 0, __GetStride(m_nWidth, m_bIsColor) * m_nHeight);
                    }
                    else
                    {
                        // 设置目标像素格式
                        m_objImageFormatConvert.SetDstFormat(GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BGR8);

                        // 获取目标像素格式Buffer大小
                        UInt64 i64DstBufferize = m_objImageFormatConvert.GetBufferSizeForConversion(objIBaseData);

                        // 设置目标有效位数
                        m_objImageFormatConvert.SetValidBits(emValidBits);

                        // 进行图像格式转换
                        m_objImageFormatConvert.Convert(objIBaseData, m_pOutBuffer, i64DstBufferize, false);
                        Marshal.Copy(m_pOutBuffer, m_byColorBuffer, 0, __GetStride(m_nWidth, m_bIsColor) * m_nHeight);
                    }

                  __UpdateBitmapForSave(m_byColorBuffer);
              }
              else
              {
                  if (__IsPixelFormat8(objIBaseData.GetPixelFormat()))
                  {
                      Marshal.Copy(objIBaseData.GetBuffer(), m_byMonoBuffer, 0, __GetStride(m_nWidth, m_bIsColor) * m_nHeight);
                  }
                  else
                  {
                      // 设置目标像素格式
                      m_objImageFormatConvert.SetDstFormat(GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_MONO8);

                      // 获取目标像素格式Buffer大小
                      UInt64 i64DstBufferize = m_objImageFormatConvert.GetBufferSizeForConversion(objIBaseData);

                      // 设置目标有效位数
                      m_objImageFormatConvert.SetValidBits(emValidBits);

                      // 进行图像格式转换
                      m_objImageFormatConvert.Convert(objIBaseData, m_pOutBuffer, i64DstBufferize, false);
                  
                      Marshal.Copy(m_pOutBuffer, m_byMonoBuffer, 0, __GetStride(m_nWidth, m_bIsColor) * m_nHeight);
                  }

                  __UpdateBitmapForSave(m_byMonoBuffer);
              }
              m_bitmapForSave.Save(strFilePath, ImageFormat.Bmp);
          }
      }

      /// <summary>
      /// 存储Raw图像
      /// </summary>
      /// <param name="objIBaseData">图像数据对象</param>
      /// <param name="strFilePath">显示图像文件名</param>
      public void SaveRaw(IBaseData objIBaseData, string strFilePath)
      {
          Stream objFileStream = new FileStream(strFilePath, FileMode.Create);
          BinaryWriter objSW = new BinaryWriter(objFileStream);

          //检查图像是否改变并更新Buffer
          __UpdateBufferSize(objIBaseData);
          
          if (null != objIBaseData)
          {
              IntPtr pBufferRaw = objIBaseData.GetBuffer();
              Marshal.Copy(pBufferRaw, m_byRawBuffer, 0, m_nPayloadSize);
          }

          objSW.Write(m_byRawBuffer);
          objSW.Close();
          objFileStream.Close();
      }

      /// <summary>
      /// 检查图像是否改变并更新Buffer
      /// </summary>
      /// <param name="objIBaseData">图像数据对象</param>
      private void __UpdateBufferSize(IBaseData objIBaseData)
      {
          if (null != objIBaseData)
          {
              if (__IsCompatible(m_bitmapForSave, m_nWidth, m_nHeight, m_bIsColor))
              {
                  m_nPayloadSize = (int)objIBaseData.GetPayloadSize();
                  m_nWidth  = (int)objIBaseData.GetWidth();
                  m_nHeight = (int)objIBaseData.GetHeight();
              }
              else
              {
                  m_nPayloadSize = (int)objIBaseData.GetPayloadSize();
                  m_nWidth  = (int)objIBaseData.GetWidth();
                  m_nHeight = (int)objIBaseData.GetHeight();

                  m_byRawBuffer = new byte[m_nPayloadSize];
                  m_byMonoBuffer = new byte[__GetStride(m_nWidth, m_bIsColor) * m_nHeight];
                  m_byColorBuffer = new byte[__GetStride(m_nWidth, m_bIsColor) * m_nHeight];

                  //更新BitmapInfo
                  m_objBitmapInfo.bmiHeader.biWidth = m_nWidth;
                  m_objBitmapInfo.bmiHeader.biHeight = m_nHeight;
                  Marshal.StructureToPtr(m_objBitmapInfo, m_pBitmapInfo, false);
              }
          }
      }

      /// <summary>
      /// 更新存储数据
      /// </summary>
      /// <param name="byBuffer">图像buffer</param>
      private void __UpdateBitmapForSave(byte[] byBuffer)
      {
          if (__IsCompatible(m_bitmapForSave, m_nWidth, m_nHeight, m_bIsColor))
          {
              __UpdateBitmap(m_bitmapForSave, byBuffer, m_nWidth, m_nHeight, m_bIsColor);
          }
          else
          {
              __CreateBitmap(out m_bitmapForSave, m_nWidth, m_nHeight, m_bIsColor);
              __UpdateBitmap(m_bitmapForSave, byBuffer, m_nWidth, m_nHeight, m_bIsColor);
          }
      }

      /// <summary>
      /// 显示图像处理
      /// </summary>
      /// <param name="byBuffer">图像数据buffer</param>
      private void __ShowImage(byte[] byBuffer)
      {
          if (null != m_pic_ShowImage)
          {
              CWin32Bitmaps.SetStretchBltMode(m_pHDC, COLORONCOLOR);
              CWin32Bitmaps.StretchDIBits(
                          m_pHDC,
                          0,
                          0,
                          m_pic_ShowImage.Width,
                          m_pic_ShowImage.Height,
                          0,
                          0,
                          m_nWidth,
                          m_nHeight,
                          byBuffer,
                          m_pBitmapInfo,
                          DIB_RGB_COLORS,
                          SRCCOPY);            
          }
      }

      /// <summary>
      /// 判断PixelFormat是否为8位
      /// </summary>
      /// <param name="emPixelFormatEntry">图像数据格式</param>
      /// <returns>true为8为数据，false为非8位数据</returns>
      private bool __IsPixelFormat8(GX_PIXEL_FORMAT_ENTRY emPixelFormatEntry)
      {
          bool bIsPixelFormat8 = false;
          uint uiPixelFormatEntry = (uint)emPixelFormatEntry;
          if ((uiPixelFormatEntry & PIXEL_FORMATE_BIT) == GX_PIXEL_8BIT)
          {
              bIsPixelFormat8 = true;
          }
          return bIsPixelFormat8;
      }
 
      /// <summary>
      /// 通过GX_PIXEL_FORMAT_ENTRY获取最优Bit位
      /// </summary>
      /// <param name="em">图像数据格式</param>
      /// <returns>最优Bit位</returns>
      public GX_VALID_BIT_LIST GetBestValudBit(GX_PIXEL_FORMAT_ENTRY emPixelFormatEntry)
      {
          GX_VALID_BIT_LIST emValidBits = GX_VALID_BIT_LIST.GX_BIT_0_7;
          switch (emPixelFormatEntry)
          {
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_MONO8:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GR8:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_RG8:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GB8:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_BG8:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_RGB8:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BGR8:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_R8:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_G8:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_B8:
                  {
                      emValidBits = GX_VALID_BIT_LIST.GX_BIT_0_7;
                      break;
                  }
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_MONO10:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_MONO10_P:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_MONO10_PACKED:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GR10:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_RG10:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GB10:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_BG10:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_BG10_P:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_BG10_PACKED:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GB10_P:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GB10_PACKED:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GR10_P:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GR10_PACKED:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_RG10_P:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_RG10_PACKED:
                  {
                      emValidBits = GX_VALID_BIT_LIST.GX_BIT_2_9;
                      break;
                  }
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_MONO12:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_MONO12_P:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_MONO12_PACKED:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GR12:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_RG12:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GB12:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_BG12:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_BG12_P:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_BG12_PACKED:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GB12_P:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GB12_PACKED:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GR12_P:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GR12_PACKED:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_RG12_P:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_RG12_PACKED:
                  {
                      emValidBits = GX_VALID_BIT_LIST.GX_BIT_4_11;
                      break;
                  }
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_MONO14:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GR14:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_RG14:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GB14:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_BG14:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_BG14_P:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GB14_P:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GR14_P:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_RG14_P:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_MONO14_P:
                  {
                      emValidBits = GX_VALID_BIT_LIST.GX_BIT_6_13;
                      break;
                  }
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_MONO16:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GR16:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_RG16:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GB16:
              case GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_BG16:
                  {
                      emValidBits = GX_VALID_BIT_LIST.GX_BIT_8_15;
                      break;
                  }
              default:
                  break;
          }
          return emValidBits;
      }

      /// <summary>
      /// 获取图像显示格式
      /// </summary>
      /// <param name="bIsColor">是否为彩色相机</param>
      /// <returns>图像的数据格式</returns>
      private PixelFormat __GetFormat(bool bIsColor)
      {
          return bIsColor ? PixelFormat.Format24bppRgb : PixelFormat.Format8bppIndexed;
      }

      /// <summary>
      /// 计算宽度所占的字节数
      /// </summary>
      /// <param name="nWidth">图像宽度</param>
      /// <param name="bIsColor">是否是彩色相机</param>
      /// <returns>图像一行所占的字节数</returns>
      private int __GetStride(int nWidth, bool bIsColor)
      {
          return bIsColor ? nWidth * 3 : nWidth;
      }

      /// <summary>
      /// 判断是否兼容
      /// </summary>
      /// <param name="bitmap">Bitmap对象</param>
      /// <param name="nWidth">图像宽度</param>
      /// <param name="nHeight">图像高度</param>
      /// <param name="bIsColor">是否是彩色相机</param>
      /// <returns>true为一样，false不一样</returns>
      private bool __IsCompatible(Bitmap bitmap, int nWidth, int nHeight, bool bIsColor)
      {
          if (bitmap == null
              || bitmap.Height != nHeight
              || bitmap.Width != nWidth
              || bitmap.PixelFormat != __GetFormat(bIsColor)
           )
          {
              return false;
          }
          return true;
      }

      /// <summary>
      /// 创建Bitmap
      /// </summary>
      /// <param name="bitmap">Bitmap对象</param>
      /// <param name="nWidth">图像宽度</param>
      /// <param name="nHeight">图像高度</param>
      /// <param name="bIsColor">是否是彩色相机</param>
      private void __CreateBitmap(out Bitmap bitmap, int nWidth, int nHeight, bool bIsColor)
      {
          bitmap = new Bitmap(nWidth, nHeight, __GetFormat(bIsColor));
          if (bitmap.PixelFormat == PixelFormat.Format8bppIndexed)
          {
              ColorPalette colorPalette = bitmap.Palette;
              for (int i = 0; i < 256; i ++)
              {
                  colorPalette.Entries[i] = Color.FromArgb(i, i, i);
              }
              bitmap.Palette = colorPalette;
          }
      }

      /// <summary>
      /// 更新和复制图像数据到Bitmap的buffer
      /// </summary>
      /// <param name="bitmap">Bitmap对象</param>
      /// <param name="nWidth">图像宽度</param>
      /// <param name="nHeight">图像高度</param>
      /// <param name="bIsColor">是否是彩色相机</param>
      private void __UpdateBitmap(Bitmap bitmap, byte[] byBuffer, int nWidth, int nHeight, bool bIsColor)
      {
          //给BitmapData加锁
          BitmapData bmpData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadWrite, bitmap.PixelFormat);

          //得到一个指向Bitmap的buffer指针
          IntPtr ptrBmp = bmpData.Scan0;
          int nImageStride = __GetStride(m_nWidth, bIsColor);
          //图像宽能够被4整除直接copy
          if (nImageStride == bmpData.Stride)
          {
              Marshal.Copy(byBuffer, 0, ptrBmp, bmpData.Stride * bitmap.Height);
          }
          else//图像宽不能够被4整除按照行copy
          {
              for (int i = 0; i < bitmap.Height; ++i)
              {
                  Marshal.Copy(byBuffer, i * nImageStride, new IntPtr(ptrBmp.ToInt64() + i * bmpData.Stride), m_nWidth);
              }
          }
          //BitmapData解锁
          bitmap.UnlockBits(bmpData);
      }

      /// <summary>
      /// 是否支持彩色
      /// </summary>
      /// <param name="bIsColorFilter">是否支持彩色</param>
        public void IsSupportColor(ref bool bIsColorFilter)
      {
          bool bIsImplemented = false;
          bool bIsMono = false;
          string strPixelFormat = "";
          uint nPixelFormatValue = 0;

          strPixelFormat = m_objIGXDevice.GetRemoteFeatureControl().GetEnumFeature("PixelFormat").GetValue();
          if (0 == string.Compare(strPixelFormat, 0, "Mono", 0, 4))
          {
              bIsMono = true;
          }
          else
          {
              bIsMono = false;
          }


          // 通过当前像素格式判断是否为彩色相机
          CDecide.GetConvertPixelFormat(strPixelFormat, ref nPixelFormatValue);
          bIsImplemented = CDecide.GetIsGray(nPixelFormatValue);
          if ((!bIsMono) && (!bIsImplemented))
          {
              bIsColorFilter = true;
          }
          else
          {
              bIsColorFilter = false;
          }
      }
    }
}


// 大恒官方示例来源：GxDecide.cs
namespace GxIAPINET.Sample.Common
{
    public static class CDecide
    {
        public const uint GVSP_PIX_MONO       = 0x01000000;   ///<判断是否为MONO格式的掩码
        public const uint GVSP_PIX_RGB        = 0x02000000;   ///<判断是否为RGB格式的掩码
        public const uint GVSP_PIX_CUSTOM     = 0x80000000;   ///<判断是否为自定义格式的掩码
        public const uint GVSP_PIX_COLOR_MASK = 0xFF000000;   ///<判断是否为彩色格式的掩码
        public const uint GVSP_PIX_ID_MASK    = 0x0000FFFF;   ///<将图像格式与下述宏定义做按位与（&）运算，可得到像素格式的ID

        /// <summary>
        /// 是否支持黑白
        /// </summary>
        public static bool GetISMono(uint nPixelFormat)
        {
            bool bIsMono = ((GVSP_PIX_COLOR_MASK & nPixelFormat) == GVSP_PIX_MONO);
            return bIsMono;
        }

        /// <summary>
        /// 是否支持彩色
        /// </summary>
        public static bool GetIsBayer(uint nPixelFormat)
        {
            bool bIsBayer = (((GVSP_PIX_ID_MASK & (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GR8) <= (GVSP_PIX_ID_MASK & nPixelFormat)
                               && (GVSP_PIX_ID_MASK & nPixelFormat) <= (GVSP_PIX_ID_MASK & (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_BG12))
                               || ((GVSP_PIX_ID_MASK & (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GR14) <= (GVSP_PIX_ID_MASK & nPixelFormat)
                               && (GVSP_PIX_ID_MASK & nPixelFormat) <= (GVSP_PIX_ID_MASK & (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_BG14))
                               || ((GVSP_PIX_ID_MASK & (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GR16) <= (GVSP_PIX_ID_MASK & nPixelFormat)
                               && (GVSP_PIX_ID_MASK & nPixelFormat) <= (GVSP_PIX_ID_MASK & (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_BG16)));
            return bIsBayer;
        }

        /// <summary>
        /// 是否支持RGB
        /// </summary>
        public static bool GetIsRGB(uint nPixelFormat)
        {
            bool bIsBayer = ((GVSP_PIX_COLOR_MASK & nPixelFormat) == GVSP_PIX_RGB);
            return bIsBayer;
        }

        /// <summary>
        /// 是否支持RGB8
        /// </summary>
        public static bool GetIsRGB8(uint nPixelFormat)
        {
            bool bIsRGB8 = ((nPixelFormat == (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_RGB8)
                           || (nPixelFormat == (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BGR8));
            return bIsRGB8;
        }

        /// <summary>
        /// 是否为黑白
        /// </summary>
        public static bool GetIsGray(uint nPixelFormat)
        {

            bool bIsGray = (GetISMono(nPixelFormat)) && (!GetIsBayer(nPixelFormat)) && (!GetIsRGB(nPixelFormat));
            return bIsGray;
        }

        /// <summary>
        /// 根据像素格式获取对应ID
        /// </summary>
        public static void GetConvertPixelFormat(String strPixelFormat, ref uint nPixelFormatValue)
        {
            switch (strPixelFormat)
            {
                case "Mono8":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_MONO8;
                    break;

                case "BayerRG8":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_RG8;
                    break;

                case "BayerGB8":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GB8;
                    break;

                case "BayerGR8":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GR8;
                    break;

                case "BayerBG8":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_BG8;
                    break;

                case "RGB8":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_RGB8;
                    break;

                case "BGR8":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BGR8;
                    break;

                case "Mono10":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_MONO10;
                    break;

                case "Mono10_Packed":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_MONO10_PACKED;
                    break;

                case "Mono10_P":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_MONO10_P;
                    break;

                case "BayerRG10":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_RG10;
                    break;

                case "BayerRG10_P":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_RG10_P;
                    break;

                case "BayerRG10_Packet":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_RG10_PACKED;
                    break;

                case "BayerGB10":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GB10;
                    break;

                case "BayerGB10_P":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GB10_P;
                    break;

                case "BayerGB10_Packet":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GB10_PACKED;
                    break;

                case "BayerGR10":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GR10;
                    break;

                case "BayerGR10_P":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GR10_P;
                    break;

                case "BayerGR10_Packet":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GR10_PACKED;
                    break;

                case "BayerBG10":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_BG10;
                    break;

                case "BayerBG10_P":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_BG10_P;
                    break;

                case "BayerBG10_Packet":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_BG10_PACKED;
                    break;

                case "Mono12":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_MONO12;
                    break;

                case "Mono12_Packed":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_MONO12_PACKED;
                    break;

                case "Mono12_P":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_MONO12_P;
                    break;

                case "BayerRG12":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_RG12;
                    break;

                case "BayerRG12_P":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_RG12_P;
                    break;

                case "BayerRG12_Packet":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_RG12_PACKED;
                    break;

                case "BayerGB12":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GB12;
                    break;

                case "BayerGB12_P":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GB12_P;
                    break;

                case "BayerGB12_Packet":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GB12_PACKED;
                    break;

                case "BayerGR12":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GR12;
                    break;

                case "BayerGR12_P":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GR12_P;
                    break;

                case "BayerGR12_Packet":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GR12_PACKED;
                    break;

                case "BayerBG12":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_BG12;
                    break;

                case "BayerBG12_P":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_BG12_P;
                    break;

                case "BayerBG12_Packet":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_BG12_PACKED;
                    break;

                case "Mono14":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_MONO14;
                    break;

                case "Mono14_P":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_MONO14_P;
                    break;

                case "BayerRG14":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_RG14;
                    break;

                case "BayerRG14_P":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_RG14_P;
                    break;

                case "BayerGB14":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GB14;
                    break;

                case "BayerGB14_P":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GB14_P;
                    break;

                case "BayerGR14":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GR14;
                    break;

                case "BayerGR14_P":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GR14_P;
                    break;

                case "BayerBG14":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_BG14;
                    break;

                case "BayerBG14_P":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_BG14_P;
                    break;

                case "Mono16":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_MONO16;
                    break;

                case "BayerRG16":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_RG16;
                    break;

                case "BayerGB16":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GB16;
                    break;

                case "BayerGR16":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_GR16;
                    break;

                case "BayerBG16":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_BAYER_BG16;
                    break;

                case "R8":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_R8;
                    break;

                case "B8":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_B8;
                    break;

                case "G8":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_G8;
                    break;

                case "YUV422_8":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_YUV422_8;
                    break;

                case "YUV422_8_UYVY":
                    nPixelFormatValue = (uint)GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_YUV422_8_UYVY;
                    break;

                default:
                    nPixelFormatValue = 0;
                    break;
            }
        }
    }
}

// 大恒官方示例来源：Win32Bitmap.cs
namespace GxIAPINET.Sample.Common
{
    public class CWin32Bitmaps
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPFILEHEADER
        {
            public ushort bfType;
            public int bfSize;
            public ushort bfReserved1;
            public ushort bfReserved2;
            public uint bfOffBits;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct RGBQUAD
        {
            public byte rgbBlue;
            public byte rgbGreen;
            public byte rgbRed;
            public byte rgbReserved;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public RGBQUAD[] bmiColors;
        }

        [DllImport("gdi32.dll", CharSet = CharSet.Auto)]
        public static extern int SetStretchBltMode(
            HDC hdc,          // handle to DC
            int iStretchMode  // bitmap stretching mode
            );

        [DllImport("gdi32.dll", CharSet = CharSet.Auto)]
        public static extern int StretchDIBits(
            HDC hdc,                   // handle to DC
            int XDest,                 // x-coord of destination upper-left corner
            int YDest,                 // y-coord of destination upper-left corner
            int nDestWidth,            // width of destination rectangle
            int nDestHeight,           // height of destination rectangle
            int XSrc,                  // x-coord of source upper-left corner
            int YSrc,                  // y-coord of source upper-left corner
            int nSrcWidth,             // width of source rectangle
            int nSrcHeight,            // height of source rectangle
            byte[] lpBits,             // bitmap bits
            LPVOID lpBitsInfo,         // bitmap data            
            UINT iUsage,               // usage options
            DWORD dwRop                // raster operation code
            );
    }
}