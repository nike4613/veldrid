using System;

namespace Veldrid.NeoDemo
{
    class Program
    {
        unsafe static void Main(string[] args)
        {
            try
            {
                Sdl2.SDL_version version;
                Sdl2.Sdl2Native.SDL_GetVersion(&version);
                new NeoDemo().Run();
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                while (true)
                {
                    Console.ReadLine();
                }
                throw;
            }
        }
    }
}
