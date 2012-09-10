#!/bin/sh

to_import=$1
labels_to_keep=$*
refDate=`date +%d-%b-%Y.%H:%M:%S | tr '[a-z]' '[A-Z]'`
viewTag=MyView
view=M:\\$viewTag\\MyVob

echo updating thirdparty $1, keeping labels $labels_to_keep

if [ ! -d working/$to_import ]
then
    echo working/$to_import not found
    exit 1
fi

if [ -f GitImporter.log ]
then
    rm GitImporter.log
fi
cd working/$to_import

vobData=$to_import.bin
if [ ! -f $vobData ]
then
    echo file $vobData not found
    exit 2
fi

if [ ! -f history.bin ]
then
    echo file history.bin not found
    exit 3
fi

export GIT_DIR=../../../../../Git/thirdparty/$to_import.git
if [ ! -d $GIT_DIR ]
then
    echo git repo $GIT_DIR not found
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
newVersions=$to_import.versions.$n

cat >cs <<EOF
element * CHECKEDOUT

element * /main/LATEST -time $refDate
EOF

cleartool setcs -tag $viewTag cs
rm cs

cleartool find $view\\thirdparty\\$to_import -ver "created_since($prevDate) && !created_since($refDate)" -print | grep -v CHECKEDOUT >$newVersions
if [ ! -s $newVersions ]
then
    echo update from $prevDate to $refDate : no new version found >>changelog
    rm $newVersions
    exit
fi

echo update $n : from $prevDate to $refDate >>changelog
echo $n >lastUpdate

perl -pi.bak -e 's/.*TOne\\tone\\//' $newVersions
rm $newVersions.bak

mv $vobData $vobData.$n

importData=$to_import.dat
if [ -f $importData ]
then
    mv $importData $importData.$n
fi
if [ -f import_$to_import.log ]
then
    mv import_$to_import.log import_$to_import.log.$n
fi

../../GitImporter -L:$vobData.$n -S:$vobData -H:history.bin -N -R:thirdparty\\$to_import -C:$view -V:$newVersions >$importData
mv ../../GitImporter.log import_$to_import.log
mv history.bin.bak history.bin.$n
perl -pi.bak -e 's/thirdparty\/'$to_import'\///g if /^(M|R|D|C) /' $importData

if [ -f git_import_$to_import.log ]
then
    mv git_import_$to_import.log git_import_$to_import.log.$n
fi
if [ -f fetch_$to_import.log ]
then
    mv fetch_$to_import.log fetch_$to_import.log.$n
fi

../../GitImporter -C:$view -F:$importData | git fast-import --import-marks=$to_import.marks --export-marks=$to_import.marks
mv ../../GitImporter.log fetch_$to_import.log

git tag | perl -e '@to_keep = @ARGV; while(<STDIN>) { chomp; $keep=0; foreach $pat (@to_keep) { $keep ||= /^$pat/i } if (!$keep) { print `git tag -d $_` } }' $labels_to_keep

configData=$to_import.config
if [ -f $configData ]
then
    mv $configData $configData.$n
fi
echo "    <ThirdPartyModule>" >$configData
echo "      <Name>$to_import</Name>" >>$configData
echo "      <Labels>" >>$configData
git tag | perl -ne 'chomp; $commit = `git rev-parse $_^0`; chomp $commit; print "        <LabelMapping>\n          <Label>$_</Label>\n          <Commit>$commit</Commit>\n        </LabelMapping>\n"' >>$configData
echo "      </Labels>" >>$configData
echo "    </ThirdPartyModule>" >>$configData
