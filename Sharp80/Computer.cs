﻿#region Using directives

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

#endregion

namespace Sharp80
{
    internal class Computer : IDisposable, ISerializable
    {
        public const ulong CLOCK_RATE = 2027520;
        private const int SERIALIZATION_VERSION = 1;

        public Clock Clock { get; private set; }
        public FloppyController FloppyController { get; private set; }
        public InterruptManager IntMgr { get; private set; }
        public PortSet Ports { get; private set; }
        public Screen Screen { get; private set; }
        public Processor.Z80 Processor { get; private set; }
        public ISound Sound { get; private set; }
        public bool HasRunYet { get; private set; }
#if CASSETTE
        private Cassette cassette;
#endif
        private bool ready;
        private bool isDisposed = false;

        // CONSTRUCTOR

        public Computer(IDXClient MainForm, ScreenDX PhysicalScreen, ulong DisplayRefreshRateInHz, bool Throttle)
        {
            ulong milliTStatesPerIRQ = CLOCK_RATE * Clock.TICKS_PER_TSTATE / 30;
            ulong milliTStatesPerSoundSample = CLOCK_RATE * Clock.TICKS_PER_TSTATE / SoundX.SAMPLE_RATE;

            HasRunYet = false;

            PhysicalScreen.Computer = this;
            Screen = new Screen(PhysicalScreen);
            
            IntMgr = new InterruptManager(this);
            Ports = new PortSet(this);
            Processor = new Processor.Z80(this);

            //Sound = new SoundNull();
            Sound = new SoundX(new GetSampleCallback(Ports.CassetteOut));
            Sound.On = Settings.SoundOn;

            Clock = new Clock(this,
                              CLOCK_RATE,
                              milliTStatesPerIRQ,
                              milliTStatesPerSoundSample,
                              new SoundEventCallback(Sound.Sample),
                              Throttle);

            Clock.ThrottleChanged += OnThrottleChanged;

            FloppyController = new FloppyController(this);


#if CASSETTE
            cassette = new Cassette(this);
            Clock.CassetteCallback = cassette.CassetteCallback;
#endif

            PhysicalScreen.Initialize(MainForm);
            Screen.SetVideoMode();

            ready = true;
        }
        
        // PROPERTIES
        
        public bool Ready
        {
            get { return ready; }
        }
        public bool IsRunning
        {
            get { return Clock.IsRunning; }
        }
        
        // RUN COMMANDS

        public void Start()
        {
            HasRunYet = true;
             
            Clock.Start();
            Sound.Mute = !Clock.Throttle;
        }
        public void Stop(bool WaitForStop)
        {
            Sound.Mute = true;
            Clock.Stop();
            if (WaitForStop)
            {
                while (Clock.IsRunning)
                    System.Threading.Thread.Sleep(0);     // make sure we're not in the middle of a cycle
            }
        }
        public void ResetButton()
        {
            IntMgr.ResetButtonLatch.Latch();
        }
        public void HardwareReset()
        {
            Stop(WaitForStop: true);
            FloppyController.HardwareReset();
            Ports.Reset();
            Processor.Reset();
            HasRunYet = false;
        }
        public void ShutDown()
        {
            ready = false;
        }
        public void StepOver()
        {
            if (!IsRunning)
                Processor.StepOver();
        }
        public void StepOut()
        {
            if (!IsRunning)
                Processor.StepOut();
        }
        public void SingleStep()
        {
            Clock.SingleStep();
        }
        public void CancelStepOverOrOut()
        {
            Processor.CancelStepOverOrOut();
        }
        public void Reset()
        {
            if (Ready)
            {
                ResetButton();
                Screen.Reset();
            }
        }
        // FLOPPY SUPPORT

        public bool HasDrivesAvailable
        {
            get { return !Ports.NoDrives; }
            set { Ports.NoDrives = !value; }
        }
        public void StartupLoadFloppies()
        {
            for (byte i = 0; i < 4; i++)
                LoadFloppy(i);
        }
        public void LoadFloppy(byte DriveNum)
        {
            LoadFloppy(DriveNum, Storage.GetDefaultDriveFileName(DriveNum));
        }
        public void LoadFloppy(byte DriveNum, string FilePath)
        {
            bool running = IsRunning;

            if (running)
                Stop(WaitForStop: true);

            if (FilePath.Length > 0)
                FloppyController.LoadFloppy(DriveNum, FilePath);
            else
                FloppyController.UnloadDrive(DriveNum);

            if (DriveNum == 0 && !HasRunYet)
                Ports.NoDrives = FloppyController.DriveIsUnloaded(0);

            if (running)
                Start();
        }
        public void LoadFloppy(byte DriveNum, Floppy Floppy)
        {
            FloppyController.LoadFloppy(DriveNum, Floppy);

            if (DriveNum == 0 && !HasRunYet)
                Ports.NoDrives = FloppyController.DriveIsUnloaded(0);
        }
        public void LoadTrsDosFloppy(byte DriveNum)
        {
            LoadFloppy(DriveNum, new DMK(Resources.TRSDOS));
        }
        public void EjectFloppy(byte DriveNum)
        {
            bool running = IsRunning;

            if (running)
                Stop(WaitForStop: true);

            FloppyController.UnloadDrive(DriveNum);

            if (running)
                Start();
        }
        
        // SNAPSHOTS

        public void SaveSnapshotFile(string FilePath)
        {
            bool running = IsRunning;

            Stop(WaitForStop: true);

            using (BinaryWriter writer = new BinaryWriter(File.Open(FilePath, FileMode.Create)))
            {
                Serialize(writer);
            }

            if (running)
                Start();
        }
        public void LoadSnapshotFile(string FilePath)
        {
            bool running = IsRunning;

            Stop(WaitForStop: true);

            using (BinaryReader reader = new BinaryReader(File.Open(FilePath, FileMode.Open, FileAccess.Read)))
            {
                Deserialize(reader);
            }

            if (running)
                Start();
        }

        // MISC

        public void Serialize(BinaryWriter Writer)
        {
            Writer.Write(SERIALIZATION_VERSION);

            Processor.Serialize(Writer);
            Clock.Serialize(Writer);
            FloppyController.Serialize(Writer);
            IntMgr.Serialize(Writer);
            Screen.Serialize(Writer);
        }
        public void Deserialize(BinaryReader Reader)
        {
            Reader.ReadInt32(); // SERIALIZATION_VERSION

            Processor.Deserialize(Reader);
            Clock.Deserialize(Reader);
            FloppyController.Deserialize(Reader);
            IntMgr.Deserialize(Reader);
            Screen.Deserialize(Reader);
        }
        public bool LoadCMDFile(string filePath)
        {
            Stop(WaitForStop: true);

            var pc = Storage.LoadCMDFile(filePath, Processor.Memory);

            if (pc.HasValue)
            {
                Processor.Jump(pc.Value);
                return true;
            }
            else
            {
                return false;
            }
        }
        public string DumpDisassembly(bool RelativeAddressesAsComments)
        {
            return Processor.GetDisassemblyDump(RelativeAddressesAsComments);
        }
        public string GetInstructionSetReport()
        {
            return Processor.GetInstructionSetReport();
        }
        public string Assemble()
        {
            return Processor.Assemble();
        }
        public bool HistoricDisassemblyMode
        {
            get { return Processor.HistoricDisassemblyMode; }
            set { Processor.HistoricDisassemblyMode = value; }
        }
        public string GetInternalsReport()
        {
            return Processor.GetInternalsReport();
        }
        public string GetClockReport(bool IncludeTickCount)
        {
            return Clock.GetInternalsReport(IncludeTickCount);
        }
        public string GetDisassembly()
        {
            return Processor.GetDisassembly();
        }
        public void NotifyKeyboardChange(SharpDX.DirectInput.Key Key, bool IsPressed)
        {
            Processor.Memory.NotifyKeyboardChange(Key, IsPressed);
        }
        public void ResetKeyboard()
        {
            Processor.Memory.ResetKeyboard();
        }
#if CASSETTE
        public void LoadCassette(string FilePath)
        {
            cassette.LoadCassete(FilePath);
        }
        public bool RewindCassette()
        {
            return cassette.Rewind();
        }
        public byte ReadCassette()
        {
            return cassette.Read();
        }
        public void CassettePower(bool MotorOn)
        {
            cassette.MotorOn = MotorOn;
        }
#endif
        public void Dispose()
        {
            if (!isDisposed)
            {
                if (!Sound.IsDisposed)
                    Sound.Dispose();
                
                Stop(WaitForStop: false);
                
                isDisposed = true;
            }
        }
        public bool IsDisposed { get { return isDisposed; } }

        // PRIVATE METHODS

        private void OnThrottleChanged(object sender, EventArgs e)
        {
            Sound.Mute = !Clock.Throttle;
        }
    }
}