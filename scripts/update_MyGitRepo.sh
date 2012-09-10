#!/bin/sh

refDate=`date +%d-%b-%Y.%H:%M:%S | tr '[a-z]' '[A-Z]'`
viewTag=MyView
view=M:\\$viewTag\\MyVob

vobData=fullVobDB.bin
if [ ! -f $vobData ]
then
    echo file $vobData not found
    exit 1
fi

if [ ! -f history.bin ]
then
    echo file history.bin not found
    exit 2
fi

export GIT_DIR=../../Git/MyGitRepo.git
if [ ! -d $GIT_DIR ]
then
    echo git repo $GIT_DIR not found
    exit 3
fi

if [ ! -f MyGitRepo.marks ]
then
    echo file MyGitRepo.marks not found
    exit 4
fi

if [ ! -f prevDate ]
then
    echo file prevDate not found
    exit 5
fi
prevDate=`cat prevDate`
echo $refDate >prevDate

if [ -f lastUpdate ]
then
    n=`cat lastUpdate`
    n=`expr $n + 1`
else
    n=1
fi

# get new versions
newVersions=newVersions.$n

cat >cs <<EOF
element * CHECKEDOUT

element * /main/LATEST -time $refDate
EOF

cleartool setcs -tag $viewTag cs
rm cs

echo finding new versions \(created between $prevDate and $refDate\)
time cleartool find $view -all -ver "created_since($prevDate) && !created_since($refDate)" -print >$newVersions
perl filter.pl $newVersions >to_import
if [ ! -s to_import ]
then
    echo update from $prevDate to $refDate : no interesting new version found
    echo update from $prevDate to $refDate : no interesting new version found >>changelog
    rm $newVersions to_import
    exit
fi

echo update $n : `wc -l to_import | perl -ne 'print $1 if /(\d+)/'` new versions from $prevDate to $refDate >>changelog
echo $n >lastUpdate

mv $vobData $vobData.$n

importData=to_import.dat.$n

GitImporter -L:$vobData.$n -S:$vobData -H:history.bin -N -C:$view -V:to_import >$importData
mv GitImporter.log import.log.$n

sevenzip='/c/Program Files/7-Zip/7z.exe'
if [ -f "$sevenzip" ]
then
    "$sevenzip" a -bd $vobData.$n.7z $vobData.$n
    rm $vobData.$n
    "$sevenzip" a -bd history.bin.$n.7z history.bin.bak
    rm history.bin.bak
else
    mv history.bin.bak history.bin.$n
fi

GitImporter -C:$view -F:$importData | git fast-import --import-marks=MyGitRepo.marks --export-marks=MyGitRepo.marks
mv GitImporter.log fetch.log.$n
