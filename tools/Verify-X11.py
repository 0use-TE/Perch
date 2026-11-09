#!/usr/bin/env python3
import os, subprocess, time, tempfile, pathlib, json, sys, select, shutil, ctypes as C
import argparse
parser = argparse.ArgumentParser(description="Run Perch in an isolated Xvfb/Openbox session; no access to the current desktop.")
parser.add_argument("--dotnet", default="dotnet")
parser.add_argument("--app", type=pathlib.Path)
options = parser.parse_args()
repo = pathlib.Path(__file__).resolve().parents[1]
out=repo/'artifacts/x11-smoke';out.mkdir(parents=True,exist_ok=True)
env=os.environ.copy()
env['XDG_SESSION_TYPE']='x11'
env['XDG_CURRENT_DESKTOP']='Openbox'
env.pop('WAYLAND_DISPLAY',None)
runroot=pathlib.Path(tempfile.mkdtemp(prefix='perch-x11-'));env['XDG_RUNTIME_DIR']=str(runroot/'runtime');os.mkdir(env['XDG_RUNTIME_DIR'],0o700)
env['XDG_CONFIG_HOME']=str(runroot/'config')
(out/'results.json').unlink(missing_ok=True)
(out/'interaction.log').unlink(missing_ok=True)
procs=[];passed=[];displayp=None;data=None
def launch(args,name,extra=None,stdout=None,**kw):
 f=open(out/(name+'.log'),'w');p=subprocess.Popen(args,env=extra or env,stdout=stdout or f,stderr=f,**kw);procs.append(p);return p
def run(*args,check=True):
 return subprocess.run(args,env=env,text=True,capture_output=True,timeout=10,check=check)
def check(condition,name):
 if not condition:raise AssertionError(name)
 passed.append(name);print('PASS',name,flush=True)
def wait_for(test,timeout=10):
 end=time.monotonic()+timeout
 while time.monotonic()<end:
  if test():return True
  time.sleep(.1)
 return False
try:
 # Use a high, unused display number so an on-demand desktop XWayland server keeps its usual :0.
 display=next(str(n) for n in range(90,200) if not pathlib.Path(f'/tmp/.X{n}-lock').exists() and not pathlib.Path(f'/tmp/.X11-unix/X{n}').exists())
 env['DISPLAY']=':'+display
 xvfb=launch(['Xvfb',env['DISPLAY'],'-screen','0','1440x900x24','-nolisten','tcp'],'xvfb')
 check(wait_for(lambda:xvfb.poll() is None and run('xdotool','getdisplaygeometry',check=False).returncode==0),'Xvfb starts')
 config=runroot/'bus.conf';config.write_text('<busconfig><type>session</type><listen>unix:tmpdir='+str(runroot)+'</listen><policy context="default"><allow send_destination="*" eavesdrop="true"/><allow receive_sender="*" eavesdrop="true"/><allow own="*"/></policy></busconfig>')
 bus=launch(['dbus-daemon','--nofork','--print-address=1','--config-file='+str(config)],'bus',stdout=subprocess.PIPE)
 if not select.select([bus.stdout],[],[],5)[0]:raise RuntimeError('D-Bus did not start')
 env['DBUS_SESSION_BUS_ADDRESS']=bus.stdout.readline().decode().strip()
 rc=runroot/'openbox.xml';rc.write_text('<openbox_config xmlns="http://openbox.org/3.4/rc"><theme><name>Clearlooks</name></theme></openbox_config>')
 wm=launch(['openbox','--config-file',str(rc)],'openbox');time.sleep(1);check(wm.poll() is None,'Openbox starts')
 launch(['xcompmgr','-a'],'compositor')
 # A plain Xlib window is enough to test focus and fullscreen. Unlike xmessage,
 # it does not require legacy bitmap fonts to be installed in a headless runner.
 x11=C.CDLL('libX11.so.6');ext=C.CDLL('libXext.so.6')
 x11.XOpenDisplay.argtypes=[C.c_char_p];x11.XOpenDisplay.restype=C.c_void_p
 x11.XDefaultRootWindow.argtypes=[C.c_void_p];x11.XDefaultRootWindow.restype=C.c_ulong
 x11.XCreateSimpleWindow.argtypes=[C.c_void_p,C.c_ulong,C.c_int,C.c_int,C.c_uint,C.c_uint,C.c_uint,C.c_ulong,C.c_ulong];x11.XCreateSimpleWindow.restype=C.c_ulong
 x11.XStoreName.argtypes=[C.c_void_p,C.c_ulong,C.c_char_p]
 x11.XMapWindow.argtypes=[C.c_void_p,C.c_ulong]
 x11.XSync.argtypes=[C.c_void_p,C.c_int]
 x11.XCloseDisplay.argtypes=[C.c_void_p]
 displayp=x11.XOpenDisplay(env['DISPLAY'].encode())
 if not displayp:raise RuntimeError('Cannot connect to the isolated X11 display')
 reference_id=x11.XCreateSimpleWindow(displayp,x11.XDefaultRootWindow(displayp),50,70,1200,700,0,0,0x334455)
 x11.XStoreName(displayp,reference_id,b'PerchReference');x11.XMapWindow(displayp,reference_id);x11.XSync(displayp,False)
 reference=str(reference_id)
 check(wait_for(lambda:reference in run('xdotool','search','--onlyvisible','--name','^PerchReference$',check=False).stdout.splitlines()),'Reference window opens without legacy fonts')
 run('xdotool','windowactivate','--sync',reference);run('xdotool','mousemove','1300','800')
 data=runroot/'data';env['PERCH_DATA']=str(data);env['PERCH_LANG']='en';env['PERCH_DBG']=str(out/'interaction.log')
 # Pin the clipboard page for an observable end-to-end history check.
 data.mkdir();(data/'state.json').write_text(json.dumps({'Settings':{'NavPinned':['music','focus','todo','notes','clipboard','system']}}))
 dotnet=options.dotnet;dll=str((options.app or repo/'src/Perch/bin/Release/net10.0/Perch.dll').resolve())
 command=[dotnet,dll] if pathlib.Path(dll).suffix=='.dll' else [dll]
 app=launch(command,'perch')
 found=[]
 def window_ready():
  ids=run('xdotool','search','--name','^Perch$',check=False).stdout.strip().splitlines()
  if ids:found.extend(ids);return True
  return False
 check(wait_for(window_ready) and app.poll() is None,'Perch opens on native X11')
 wid=found[-1];time.sleep(1)
 check(run('xdotool','getwindowfocus').stdout.strip()!=wid,'Startup leaves focus in the other app')
 class Rect(C.Structure):_fields_=[('x',C.c_short),('y',C.c_short),('width',C.c_ushort),('height',C.c_ushort)]
 ext.XShapeGetRectangles.argtypes=[C.c_void_p,C.c_ulong,C.c_int,C.POINTER(C.c_int),C.POINTER(C.c_int)];ext.XShapeGetRectangles.restype=C.POINTER(Rect)
 x11.XFree.argtypes=[C.c_void_p]
 def shape():
  n=C.c_int();order=C.c_int();ptr=ext.XShapeGetRectangles(displayp,int(wid),2,C.byref(n),C.byref(order));rects=[(r.x,r.y,r.width,r.height) for r in ptr[:n.value]];x11.XFree(ptr);return rects
 def height():return max((y+h for x,y,w,h in shape()),default=0)
 check(height()<60,'Compact input region excludes the transparent host')
 run('import','-window','root',str(out/'compact.png'))
 run('xdotool','mousemove','720','16');check(wait_for(lambda:height()>250),'Hover expands the island')
 run('import','-window','root',str(out/'expanded.png'))
 check(run('xdotool','getwindowfocus').stdout.strip()!=wid,'Hover does not steal focus')
 run('xdotool','mousemove','1300','800');check(wait_for(lambda:height()<60),'Leaving collapses the island')
 run('xdotool','keydown','ctrl+alt+i');check(wait_for(lambda:height()>250),'Global shortcut opens the island')
 time.sleep(1.5);check(height()>250,'Holding a global shortcut does not repeat')
 run('xdotool','keyup','ctrl+alt+i')
 run('xdotool','key','ctrl+alt+i');check(wait_for(lambda:height()<60),'Global shortcut closes the island')
 run('xdotool','windowactivate','--sync',reference)
 run('xdotool','windowstate','--add','FULLSCREEN',reference)
 check(wait_for(lambda:not run('xdotool','search','--onlyvisible','--name','^Perch$',check=False).stdout.strip()),'A fullscreen app hides the island')
 run('xdotool','windowstate','--remove','FULLSCREEN',reference)
 check(wait_for(lambda:bool(run('xdotool','search','--onlyvisible','--name','^Perch$',check=False).stdout.strip())),'Leaving fullscreen restores the island')
 second=run(*command);check(second.returncode==0 and wait_for(lambda:height()>250),'Second launch wakes the existing instance')
 check(len(run('xdotool','search','--name','^Perch$').stdout.strip().splitlines())==1,'Second launch does not create another window')
 run('xdotool','key','ctrl+alt+n');time.sleep(.7)
 run('xdotool','type','--clearmodifiers','--delay','20','X11 persistence check');time.sleep(1.3)
 saved=json.loads((data/'state.json').read_text());check(any(n.get('Title')=='X11 persistence check' for n in saved.get('Notes',[])),'New-note shortcut focuses input and persists typed text')
 run('import','-window','root',str(out/'note.png'))
 def copy(text):
  owner=launch(['xclip','-selection','clipboard','-in','-quiet'],'clipboard',stdin=subprocess.PIPE)
  owner.stdin.write(text.encode());owner.stdin.close();time.sleep(.4)
 copy('Perch X11 first clipboard item')
 copy('Perch X11 second clipboard item')
 run('xdotool','mousemove','730','40','click','1');time.sleep(.6)
 run('import','-window','root',str(out/'clipboard.png'))
 run('xdotool','mousemove','500','195','click','1');time.sleep(.3)
 check(run('xclip','-selection','clipboard','-out').stdout=='Perch X11 first clipboard item','XFixes clipboard history captures and restores text')
 # Open Settings via its button; its native window must render without Windows backdrop APIs.
 run('xdotool','mousemove','1082','40','click','1');time.sleep(1)
 settings=run('xdotool','search','--name','Settings',check=False).stdout.strip();check(bool(settings),'Settings opens on X11')
 run('import','-window','root',str(out/'settings.png'))
 logs='\n'.join(p.read_text() for p in data.rglob('*.log'));check('ERROR' not in logs,'No application errors during desktop interactions')
 print('DATA',data,flush=True)
finally:
 failure=sys.exc_info()[1]
 (out/'results.json').write_text(json.dumps({'display':env.get('DISPLAY'),'checks':passed,'data':str(data) if data else None,'error':str(failure) if failure else None},indent=2))
 if displayp:x11.XCloseDisplay(displayp)
 for p in reversed(procs):
  if p.poll() is None:p.terminate()
 for p in procs:
  try:p.wait(timeout=3)
  except subprocess.TimeoutExpired:p.kill()
 if data and (data/'logs').exists():shutil.copytree(data/'logs',out/'app-logs',dirs_exist_ok=True)
