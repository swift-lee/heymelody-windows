#import <AppKit/AppKit.h>
#import <CoreLocation/CoreLocation.h>
#include <cmath>
#include <cstdint>

// Values mirror the private bridge representation of HeyMelody.Platform.LocationAccess.
enum HMLocationAccess { HMUnknown=0, HMAllowed=1, HMDenied=2, HMSystemDisabled=3,
                        HMRestricted=4, HMUnavailable=5, HMFailed=6 };
typedef void (*HMLocationCallback)(void*, int64_t, int, int, double, double, double, double, int);

@class HMLocationBridge;
@interface HMLocationRead : NSObject <CLLocationManagerDelegate>
@property(nonatomic, weak) HMLocationBridge* owner;
@property(nonatomic, strong) CLLocationManager* manager;
@property(nonatomic) int64_t operation;
@property(nonatomic) BOOL finished;
- (void)finishWithLocation:(CLLocation*)location access:(int)access error:(int)error;
- (void)cancel;
@end

@interface HMLocationBridge : NSObject <CLLocationManagerDelegate>
@property(nonatomic) HMLocationCallback callback;
@property(nonatomic) void* context;
@property(nonatomic, strong) CLLocationManager* manager;
@property(nonatomic, strong) NSMutableSet<NSNumber*>* permissionRequests;
@property(nonatomic, strong) NSMutableSet<NSNumber*>* operations;
@property(nonatomic, strong) NSMutableDictionary<NSNumber*, HMLocationRead*>* reads;
@property(nonatomic) BOOL active;
@property(nonatomic) int lastAccess;
@property(nonatomic) BOOL hasAccess;
- (void)emitOperation:(int64_t)operation kind:(int)kind access:(int)access
             location:(CLLocation*)location error:(int)error;
- (void)refreshAccess:(void (^)(int))completion;
- (void)permission:(int64_t)operation request:(BOOL)request;
- (void)read:(int64_t)operation;
- (void)cancel:(int64_t)operation;
- (void)shutdown;
@end

static void HMLocationOnMain(dispatch_block_t action) {
    dispatch_async(dispatch_get_main_queue(), ^{ @autoreleasepool { action(); } });
}
static int HMLocationAuthorization(CLAuthorizationStatus status, BOOL servicesEnabled) {
    if (!servicesEnabled) return HMSystemDisabled;
    switch (status) {
        case kCLAuthorizationStatusAuthorizedAlways:
        case kCLAuthorizationStatusAuthorizedWhenInUse: return HMAllowed;
        case kCLAuthorizationStatusDenied: return HMDenied;
        case kCLAuthorizationStatusRestricted: return HMRestricted;
        default: return HMUnknown;
    }
}

@implementation HMLocationBridge
- (instancetype)init {
    if ((self=[super init])) {
        _active=YES;
        _permissionRequests=[NSMutableSet new];
        _operations=[NSMutableSet new];
        _reads=[NSMutableDictionary new];
        _manager=[CLLocationManager new];
        _manager.delegate=self;
    }
    return self;
}
- (void)emitOperation:(int64_t)operation kind:(int)kind access:(int)access
             location:(CLLocation*)location error:(int)error {
    if (!_active || !_callback) return;
    if (operation != 0) {
        if (![_operations containsObject:@(operation)]) return;
        [_operations removeObject:@(operation)];
    }
    _callback(_context,operation,kind,access,
              location ? location.coordinate.latitude : 0,
              location ? location.coordinate.longitude : 0,
              location ? location.horizontalAccuracy : 0,
              location ? location.timestamp.timeIntervalSince1970 : 0,error);
}
- (void)refreshAccess:(void (^)(int))completion {
    // The synchronous global-service query can block. Only that query runs off the main queue;
    // all CLLocationManager creation, reads, delegate work and destruction stay on the main RunLoop.
    dispatch_async(dispatch_get_global_queue(QOS_CLASS_UTILITY,0), ^{
        BOOL enabled=[CLLocationManager locationServicesEnabled];
        HMLocationOnMain(^{
            if (!self.active) return;
            int access=HMLocationAuthorization(self.manager.authorizationStatus,enabled);
            if (!self.hasAccess || access != self.lastAccess) {
                self.hasAccess=YES; self.lastAccess=access;
                [self emitOperation:0 kind:0 access:access location:nil error:0];
            }
            completion(access);
        });
    });
}
- (void)permission:(int64_t)operation request:(BOOL)request {
    [_operations addObject:@(operation)];
    [self refreshAccess:^(int access) {
        if (![self.operations containsObject:@(operation)]) return;
        if (!request || access != HMUnknown) {
            [self emitOperation:operation kind:0 access:access location:nil error:0];
            return;
        }
        // Missing purpose text must fail without asking CoreLocation to display an invalid prompt.
        id purpose=NSBundle.mainBundle.infoDictionary[@"NSLocationUsageDescription"];
        if (![purpose isKindOfClass:NSString.class] || ![(NSString*)purpose length]) {
            [self emitOperation:operation kind:0 access:HMUnavailable location:nil error:0];
            return;
        }
        [self.permissionRequests addObject:@(operation)];
        // Accessory (LSUIElement) applications may activate without creating an application-owned window.
        [NSApp activateIgnoringOtherApps:YES];
        [self.manager requestWhenInUseAuthorization];
    }];
}
- (void)read:(int64_t)operation {
    [_operations addObject:@(operation)];
    [self refreshAccess:^(int access) {
        if (![self.operations containsObject:@(operation)]) return;
        if (access != HMAllowed) {
            [self emitOperation:operation kind:2 access:access location:nil error:3];
            return;
        }
        HMLocationRead* read=[HMLocationRead new];
        read.owner=self; read.operation=operation;
        read.manager=[CLLocationManager new]; read.manager.delegate=read;
        read.manager.desiredAccuracy=kCLLocationAccuracyHundredMeters;
        // A silent background read must never initiate the system consent prompt.
        if (HMLocationAuthorization(read.manager.authorizationStatus,YES) != HMAllowed) {
            read.manager.delegate=nil;
            [self emitOperation:operation kind:2 access:HMUnknown location:nil error:3];
            return;
        }
        self.reads[@(operation)]=read;
        [read.manager requestLocation];
        __weak HMLocationRead* weakRead=read;
        dispatch_after(dispatch_time(DISPATCH_TIME_NOW,15*NSEC_PER_SEC),dispatch_get_main_queue(), ^{
            HMLocationRead* current=weakRead;
            if (current && !current.finished) [current finishWithLocation:nil access:HMAllowed error:1];
        });
    }];
}
- (void)locationManagerDidChangeAuthorization:(CLLocationManager*)manager {
    [self refreshAccess:^(int access) {
        if (access != HMUnknown) {
            NSArray<NSNumber*>* waiting=self.permissionRequests.allObjects;
            [self.permissionRequests removeAllObjects];
            for (NSNumber* operation in waiting)
                [self emitOperation:operation.longLongValue kind:0 access:access location:nil error:0];
        }
        if (access != HMAllowed) {
            for (HMLocationRead* read in self.reads.allValues)
                [read finishWithLocation:nil access:access error:3];
        }
    }];
}
- (void)cancel:(int64_t)operation {
    [_operations removeObject:@(operation)];
    [_permissionRequests removeObject:@(operation)];
    HMLocationRead* read=_reads[@(operation)];
    [read cancel];
    [_reads removeObjectForKey:@(operation)];
}
- (void)shutdown {
    _active=NO;
    [_operations removeAllObjects];
    [_permissionRequests removeAllObjects];
    for (HMLocationRead* read in _reads.allValues) [read cancel];
    [_reads removeAllObjects];
    _manager.delegate=nil; [_manager stopUpdatingLocation]; _manager=nil; _callback=nullptr;
}
@end

@implementation HMLocationRead
- (void)cancel {
    _finished=YES;
    _manager.delegate=nil; [_manager stopUpdatingLocation]; _manager=nil;
}
- (void)finishWithLocation:(CLLocation*)location access:(int)access error:(int)error {
    if (_finished) return;
    HMLocationBridge* owner=_owner;
    int64_t operation=_operation;
    [self cancel];
    [owner.reads removeObjectForKey:@(operation)];
    [owner emitOperation:operation kind:location ? 1 : 2 access:access location:location error:error];
}
- (void)locationManager:(CLLocationManager*)manager didUpdateLocations:(NSArray<CLLocation*>*)locations {
    CLLocation* newest=nil;
    for (CLLocation* location in locations) {
        double age=-location.timestamp.timeIntervalSinceNow;
        if (location.horizontalAccuracy < 0 || !std::isfinite(location.horizontalAccuracy)
            || !CLLocationCoordinate2DIsValid(location.coordinate) || !std::isfinite(age) || age > 60 || age < -1) continue;
        if (!newest || [location.timestamp compare:newest.timestamp] == NSOrderedDescending) newest=location;
    }
    if (newest) [self finishWithLocation:newest access:HMAllowed error:0];
    // A stale/invalid cached fix must not become a successful record. The bounded read can time out.
}
- (void)locationManager:(CLLocationManager*)manager didFailWithError:(NSError*)error {
    if ([error.domain isEqualToString:kCLErrorDomain] && error.code == kCLErrorDenied) {
        __weak HMLocationRead* weakSelf=self;
        [_owner refreshAccess:^(int access) {
            [weakSelf finishWithLocation:nil access:access error:3];
        }];
    } else {
        [self finishWithLocation:nil access:HMAllowed
                            error:([error.domain isEqualToString:kCLErrorDomain] && error.code == kCLErrorLocationUnknown) ? 2 : 3];
    }
}
@end

static NSMutableDictionary<NSValue*, HMLocationBridge*>* HMLocationInstances;
static HMLocationBridge* HMLocationInstance(void* context) {
    return HMLocationInstances[[NSValue valueWithPointer:context]];
}
extern "C" {
void hm_location_start(HMLocationCallback callback,void* context) {
    HMLocationOnMain(^{
        if (!HMLocationInstances) HMLocationInstances=[NSMutableDictionary new];
        HMLocationBridge* bridge=[HMLocationBridge new];
        bridge.callback=callback; bridge.context=context;
        HMLocationInstances[[NSValue valueWithPointer:context]]=bridge;
    });
}
void hm_location_request_permission(void* context,int64_t operation) {
    HMLocationOnMain(^{ [HMLocationInstance(context) permission:operation request:YES]; });
}
void hm_location_check_permission(void* context,int64_t operation) {
    HMLocationOnMain(^{ [HMLocationInstance(context) permission:operation request:NO]; });
}
void hm_location_get(void* context,int64_t operation) {
    HMLocationOnMain(^{ [HMLocationInstance(context) read:operation]; });
}
void hm_location_cancel(void* context,int64_t operation) {
    HMLocationOnMain(^{ [HMLocationInstance(context) cancel:operation]; });
}
void hm_location_shutdown(void* context) {
    HMLocationOnMain(^{
        [HMLocationInstance(context) shutdown];
        [HMLocationInstances removeObjectForKey:[NSValue valueWithPointer:context]];
    });
}
}
