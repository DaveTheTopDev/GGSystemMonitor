# GGSystemMonitor
![Apex Pro TKL OLED Display of Temperatures](https://i.imgur.com/1GnrzHC.jpeg "Apex Pro TKL OLED Display of Temperatures")

This is a custom application for SteelSeries GG software that imitates the long missing SystemMonitor application. It will display your CPU and GPU temperatures on the keyboard OLED screen, updating every two seconds. This will allow you to maintain an up to date GG software and carry over through updates, meaning you do not need to forcefully downgrade.

**This is specifically designed for the SteelSeries Apex Pro keyboard.**

The application is a .NET app written in C#. It was designed on Windows and uses Windows Forms libraries, meaning it will not work on other operating systems (At least I believe, it is untested).

To monitor the system temperatures, LibreHardwareMonitor is used and included. On many systems, the CPU temperature sensors are protected by an extra layer of security. **If you are seeing on your display that the CPU temperature is reading as N/A**, then you must run the exe file as administrator to fix this!

The script itself runs in the background and uses only ~11 MB of memory.

There may be issues with reading the CPU temperature on certain systems. I myself designed it for my intel CPU system. In theory it should be able to work on any CPU, please create an issue if you face any problems!

## Features

The CPU average core temperature will be displayed on the first line; the fallback is the CPU package sensor, the extra fallback is to manually calculate the average from the cores. The GPU core temperature will be displayed on the second line.

### Settings File
There is a settings file in the applications folder called "settings.json" which can be used to configure all of the settings of the application. You can toggle the temperature warning indicators, the caps lock indicator, the GPU thermal paste monitoring feature, and modify the critical and warning temperature levels. By default, the CPU and GPU critical and warning temperatures are set to "AUTO" meaning that the program will automatically determine the critical temperature levels based on your hardware and the manufacturer's critical temperature rating.

### Caps Lock Indicator
On the top line, right indented, it will display a '🡅' icon if your keyboard is in caps lock mode.

### Warning Indicators
A large list of commonly known CPUs and GPUs is included within the script. When the script is started, it will detect your hardware and find the manufacturer's critical temperature levels. When the temperature for either your CPU or GPU reaches within **15°C** of the critical temperature, a small '⚠' icon will start blinking on the far right of the line to indicate that the hardware is reaching potentially dangerous levels of heat. When the temperature reaches within **7°C** of the critical temperature, a faster '🔥' icon will start blinking instead, indicating that the hardware is reaching very close to the critical temperature.

### GPU Thermal Paste Health Monitoring
The script will also monitor your GPU's hot spot sensor. When a difference of **15°C** is reached between the GPU core and hot spot, the bottom line of the OLED will begin to display scrolling text, indicating to the user that there is an issue with the thermal cooling within the GPU and that serious damage can happen if it is not checked. It will also swap between the scrolling text momentarily to display the current GPU core temperature and then the current GPU hot spot temperature for the user to assess the levels.

## Automatic Startup Task
This section is instructions on making the custom application run upon starting your computer so that it is always displaying your system temperatures on the OLED without the need to run the exe file everytime. **These instructions are for Windows systems ONLY, specifically for Windows 10 but can be followed along roughly for other windows systems.**
### Step 1:
**Download the zip file from the Releases section and extract it to somewhere on your computer.** For these instructions it will be assumed that you would have extracted the folder and dragged out the GGSystemMonitor-[latest-version] folder (Next to the README.txt file) directly into your C drive directory.
### Step 2:
**Open Task Scheduler** (Press the windows button and search for "Task Scheduler")

Select **"Create Task"**
### Step 3:
**Name** the task whatever you want (Recommended name: GGSystemMonitor).

**Check the box "Run with highest privileges"** so that our script will be run as administrator to read CPU temperatures.

**Set the configuration** for your windows system (e.g. Windows 10 if on Windows 10).
### Step 4:
Open the **Triggers** tab.

Click **New**.

**Select "At log on of any user"** for the begin task selection. We choose this over "At startup" because there is an issue with the caps-lock indicator not appearing if the task is initialized before a user is signed in.

**Select "Delay task for:"** and put in 30 seconds. We put a delay after the system starts up to allow for the GG software to start first.
### Step 5:
Open the **Actions** tab.

Click **New**.

**Select "Start a program"** for the action.

Now click **Browse** for the Program/script section and select the GGSystemMonitor.exe file where you saved it. Where you save it is important, for these instructions as mentioned earlier, we will go under the assumption that it was saved directly into the C drive so the path would look like "C:\GGSystemMonitor-V2.1.0\GGSystemMonitor.exe"

Next, you will need to copy the system path to the folder you have the exe file in and put it in the **Start in:** section. So for this tutorial it would be "C:\GGSystemMonitor-V2.1.0" the folder where our exe file is.

### Step 6:
Open the **Conditions** tab.

**Uncheck the box "Start the task only if the computer is on AC power"** this setting is more for labtops but just in case we uncheck it here.
### Step 7:
Open the **Settings** tab.

**Ensure that "Allow task to be run on demand" is checked.**

**Finally, click the Ok button.**

It is done! Now whenever you log on, after 30 seconds it will start displaying your CPU and GPU temperatures to your keyboards OLED display, updating every two seconds.

## Updating
When updates are released, it is very simple to update your app.

**Open Task Manager and locate the GGSystemMonitor.exe service running, select it, and force close it.** This will allow you to remove your old version.

Simply download the new zip release from the releases section, **delete your old folder of your old version, and extract the folder into the same place.**

Next, open your **Windows task scheduler and locate your custom startup task.**

Open the properties of your custom task.

Navigate to the **Actions** tab, select your startup action, and click **Edit.**

Click **Browse** and navigate to the newly updated apps folder and **select the GGSystemMonitor.exe file.**

**Remember to also change the Start In setting**, set it to start in the apps folder (The GGSystemMonitor.exe files folder).

And now it is updated!
